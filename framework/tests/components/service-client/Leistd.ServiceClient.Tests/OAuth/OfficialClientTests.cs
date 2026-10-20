using System.Net;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using System.Net.Http.Headers;
using Leistd.ServiceClient.Abstractions;
using Leistd.ServiceClient.Exceptions;
using Leistd.ServiceClient.OAuth;
using Leistd.ServiceClient.OAuth.Handlers;
using Leistd.ServiceClient.OAuth.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Http;
using OpenIddict.Client.SystemNetHttp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.ServiceClient.Tests.OAuth;

// 官方 SDK 经 TestServer 的 HTTP 管道发现与认证，不替换协议服务；跨进程网络由 E2E 覆盖。
public sealed class OfficialClientTests : IAsyncLifetime
{
    private WebApplication host = null!;
    private int machineRequests;
    private int tokenRequests;
    private int exchangeRequests;
    private readonly List<IFormCollection> exchanges = [];
    // 合并并发取令牌的前提是请求真的重叠：令牌端点等到指定数量的业务请求都已在途、挂起等待，才发出令牌
    private int requiredInFlight;
    private int inFlight;
    private bool tokenIssuedWhileAllInFlight;
    private readonly TaskCompletionSource allInFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.WebHost.UseTestServer();
        host = builder.Build();
        host.MapGet("/.well-known/openid-configuration", (HttpContext context) => Results.Json(new
        {
            issuer = $"http://{context.Request.Host}/", token_endpoint = $"http://{context.Request.Host}/connect/token",
            jwks_uri = $"http://{context.Request.Host}/jwks", response_types_supported = new[] { "code" },
            subject_types_supported = new[] { "public" }, id_token_signing_alg_values_supported = new[] { "RS256" },
            token_endpoint_auth_methods_supported = new[] { "client_secret_post" },
            grant_types_supported = new[] { "client_credentials", "urn:ietf:params:oauth:grant-type:token-exchange" }
        }));
        using var rsa = RSA.Create(2048);
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(rsa));
        host.MapGet("/jwks", () => Results.Json(new { keys = new[] { new { kty = "RSA", e = jwk.E, n = jwk.N, use = "sig", kid = "test" } } }));
        host.MapPost("/connect/token", async (HttpContext context) =>
        {
            Interlocked.Increment(ref tokenRequests);
            var form = await context.Request.ReadFormAsync();
            if (form["client_id"] != "orders-api" || form["client_secret"] != "test-secret")
                return Results.Json(new { error = "invalid_client" }, statusCode: 400);
            if (requiredInFlight > 0)
            {
                // 上限兜底：凑不齐时照常发令牌，由断言 tokenIssuedWhileAllInFlight 报告
                await Task.WhenAny(allInFlight.Task, Task.Delay(TimeSpan.FromSeconds(10)));
                tokenIssuedWhileAllInFlight = allInFlight.Task.IsCompleted;
            }
            if (form["grant_type"] == "client_credentials")
            {
                var serial = Interlocked.Increment(ref machineRequests);
                return Results.Json(new { access_token = $"machine-{serial}", token_type = "Bearer", expires_in = 600 });
            }
            lock (exchanges) exchanges.Add(form);
            var exchangeSerial = Interlocked.Increment(ref exchangeRequests);
            return Results.Json(new { access_token = $"exchange-{exchangeSerial}", token_type = "Bearer", expires_in = 120,
                issued_token_type = "urn:ietf:params:oauth:token-type:access_token" });
        });
        await host.StartAsync();
    }

    public Task DisposeAsync() => host.DisposeAsync().AsTask();

    private ServiceProvider Caller(bool exchange, Accessor? accessor = null, bool reject = false,
        Destination? destination = null, IDistributedCache? distributed = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddConsole());
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new ProtocolTransport(host.GetTestServer()));
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new InFlightProbe(this));
        if (distributed is not null) services.AddSingleton(distributed);
        services.AddServiceAuthentication(value =>
        {
            value.Authority = "http://localhost/"; value.ClientId = "orders-api";
            value.ClientSecret = reject ? "wrong-secret" : "test-secret";
        });
        services.AddSingleton<IUserAccessTokenAccessor>(accessor ?? new Accessor("subject-one"));
        var http = services.AddHttpClient("Billing").ConfigurePrimaryHttpMessageHandler(() => destination ?? new Destination());
        if (exchange) http.AddTokenExchange(value => { value.Audience = "billing-api"; value.Scope = "billing-api"; });
        else http.AddClientCredentials(value => value.Scope = "billing-api");
        return services.BuildServiceProvider();
    }

    private sealed class Accessor(string? token) : IUserAccessTokenAccessor
    {
        internal string? Token { get; set; } = token;
        public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Token);
    }

    private sealed class ProtocolTransport(TestServer server) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            if (!builder.Name!.StartsWith(typeof(OpenIddictClientSystemNetHttpOptions).Assembly.GetName().Name!, StringComparison.Ordinal)) return;
            builder.PrimaryHandler.Dispose();
            builder.PrimaryHandler = server.CreateHandler();
        };
    }

    // 包在业务客户端最外层：内层管道返回未完成的任务即视为该请求已在途挂起
    private sealed class InFlightProbe(OfficialClientTests owner) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            if (builder.Name == "Billing") builder.AdditionalHandlers.Insert(0, new Counter(owner));
        };

        private sealed class Counter(OfficialClientTests owner) : DelegatingHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = base.SendAsync(request, cancellationToken);
                if (owner.requiredInFlight > 0 && Interlocked.Increment(ref owner.inFlight) >= owner.requiredInFlight)
                    owner.allInFlight.TrySetResult();
                return response;
            }
        }
    }

    private sealed class Destination(bool unauthorized = false) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(unauthorized ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            { Content = new StringContent(request.Headers.Authorization?.Parameter ?? "") });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unauthorized_is_not_replayed_and_distributed_delete_failure_does_not_mask_it(bool exchange)
    {
        var distributed = new RejectDistributedCache { RejectRemoval = true };
        var destination = new Destination(unauthorized: true);
        await using var provider = Caller(exchange, destination: destination, distributed: distributed);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("Billing");
        using var first = await client.PostAsync("https://billing.test/", new StringContent("first"));
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(exchange ? "exchange-1" : "machine-1", await first.Content.ReadAsStringAsync());
        Assert.Equal(1, destination.Requests);
        Assert.Equal(1, tokenRequests);
        Assert.Equal(1, distributed.Removals);
        using var second = await client.PostAsync("https://billing.test/", new StringContent("second"));
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal(exchange ? "exchange-2" : "machine-2", await second.Content.ReadAsStringAsync());
        Assert.Equal(2, destination.Requests);
        Assert.Equal(2, tokenRequests);
        Assert.Equal(2, distributed.Removals);
        Assert.Equal(0, distributed.Reads + distributed.Writes);
    }

    [Fact]
    public async Task Cache_invalidation_preserves_request_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var distributed = new RejectDistributedCache { OnRemove = () => cancellation.Cancel() };
        var services = new ServiceCollection();
        services.AddLogging(); services.AddSingleton<IDistributedCache>(distributed); services.AddHybridCache();
        await using var provider = services.BuildServiceProvider();
        var cache = new TokenCache(provider.GetRequiredService<HybridCache>(), provider.GetRequiredService<ILogger<TokenCache>>());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.RemoveAsync(TokenCache.Key("cancel"), cancellation.Token).AsTask());
    }

    [Fact]
    public async Task Machine_requests_discover_authenticate_and_coalesce()
    {
        await using var provider = Caller(exchange: false);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("Billing");
        requiredInFlight = 16;
        var tokens = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => client.GetStringAsync("https://billing.test/")));
        Assert.All(tokens, token => Assert.Equal("machine-1", token));
        Assert.True(tokenIssuedWhileAllInFlight, "The token was issued before all 16 requests were in flight.");
        Assert.Equal("machine-1", await client.GetStringAsync("https://billing.test/"));
        Assert.Equal(1, machineRequests);
    }

    [Fact]
    public async Task Exchange_uses_current_subject_target_and_full_subject_cache_key()
    {
        var accessor = new Accessor("subject-one");
        await using var provider = Caller(exchange: true, accessor);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("Billing");
        requiredInFlight = 16;
        var tokens = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => client.GetStringAsync("https://billing.test/")));
        Assert.All(tokens, token => Assert.Equal("exchange-1", token));
        Assert.True(tokenIssuedWhileAllInFlight, "The token was issued before all 16 requests were in flight.");
        accessor.Token = "subject-two";
        Assert.Equal("exchange-2", await client.GetStringAsync("https://billing.test/"));
        Assert.Equal(2, exchangeRequests);
        Assert.Equal("subject-one", exchanges[0]["subject_token"]);
        Assert.Equal("subject-two", exchanges[1]["subject_token"]);
        Assert.All(exchanges, form => { Assert.Equal("billing-api", form["audience"]); Assert.Equal("billing-api", form["scope"]); });
        Assert.Equal(0, machineRequests);
    }

    [Fact]
    public async Task Missing_user_and_preset_credentials_fail_without_machine_fallback()
    {
        await using var provider = Caller(exchange: true, new Accessor(null));
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("Billing");
        await Assert.ThrowsAsync<ServiceClientException>(() => client.GetAsync("https://billing.test/"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "arbitrary");
        await Assert.ThrowsAsync<ServiceClientException>(() => client.GetAsync("https://billing.test/"));
        Assert.Equal(0, machineRequests + exchangeRequests);
    }

    [Fact]
    public async Task Protocol_errors_are_classified_and_failures_are_not_cached()
    {
        await using var provider = Caller(exchange: false, reject: true);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("Billing");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = await Assert.ThrowsAsync<ServiceClientException>(() => client.GetAsync("https://billing.test/"));
            Assert.Equal(ServiceClientFailureKind.RemoteFailure, error.FailureKind);
            Assert.Contains("invalid_client", error.Message);
        }
        Assert.Equal(2, tokenRequests);
    }

    [Fact]
    public void Binding_precedes_delegate_and_named_paths_are_distinct()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Custom:Auth:Authority"] = "http://identity.test/", ["Custom:Auth:ClientId"] = "orders-api",
            ["Custom:Auth:ClientSecret"] = "secret", ["Leistd:ServiceClients:Billing:TokenExchange:Audience"] = "billing-api",
            ["Leistd:ServiceClients:Billing:TokenExchange:Scope"] = "original",
            ["Leistd:ServiceClients:Identity:Scope"] = "tenant-routing.read"
        }).Build();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IConfiguration>(configuration);
        services.AddServiceAuthentication(configSectionPath: "Custom:Auth");
        services.AddHttpClient("Billing").AddTokenExchange(value => value.Scope = "billing-api");
        services.AddHttpClient("Identity").AddClientCredentials();
        using var provider = services.BuildServiceProvider();
        Assert.Equal("billing-api", provider.GetRequiredService<IOptionsMonitor<TokenExchangeOptions>>().Get("Billing").Scope);
        Assert.Equal("tenant-routing.read", provider.GetRequiredService<IOptionsMonitor<ClientCredentialsOptions>>().Get("Identity").Scope);
        Assert.Throws<InvalidOperationException>(() => services.AddHttpClient("Billing").AddClientCredentials());
    }

    [Fact]
    public void Custom_validation_reports_actual_configuration_path()
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddServiceAuthentication(configSectionPath: "Custom:Auth");
        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ServiceAuthenticationOptions>>().Value);
        Assert.Contains("Custom:Auth:Authority", exception.Message);
        Assert.Contains("Custom:Auth:ClientId", exception.Message);
        Assert.Contains("Custom:Auth:ClientSecret", exception.Message);
    }

    [Fact]
    public void Named_exchange_validation_reports_all_actual_keys_and_skips_other_clients()
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddHttpClient("BrokenExchange").AddTokenExchange(configSectionPath: "Custom:Exchange");
        services.AddHttpClient("ValidExchange").AddTokenExchange(value => { value.Audience = "api"; value.Scope = "api"; });
        using var provider = services.BuildServiceProvider();
        var exchanges = provider.GetRequiredService<IOptionsMonitor<TokenExchangeOptions>>();
        var exchange = Assert.Throws<OptionsValidationException>(() => exchanges.Get("BrokenExchange"));
        Assert.Contains("Custom:Exchange:Audience", exchange.Message);
        Assert.Contains("Custom:Exchange:Scope", exchange.Message);
        Assert.Equal("api", exchanges.Get("ValidExchange").Audience);
    }

    private sealed class RejectDistributedCache : IDistributedCache
    {
        internal int Reads;
        internal int Writes;
        internal int Removals;
        internal bool RejectRemoval;
        internal Action? OnRemove;
        // HybridCache 初始化会读取全局标签版本；此处只追踪 Bearer 条目。
        public byte[]? Get(string key) { if (!key.StartsWith("leistd:oauth:", StringComparison.Ordinal)) return null; Interlocked.Increment(ref Reads); throw new InvalidOperationException("Distributed read."); }
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) { if (!key.StartsWith("leistd:oauth:", StringComparison.Ordinal)) return Task.FromResult<byte[]?>(null); Interlocked.Increment(ref Reads); throw new InvalidOperationException("Distributed read."); }
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) { if (!key.StartsWith("leistd:oauth:", StringComparison.Ordinal)) return; Interlocked.Increment(ref Writes); throw new InvalidOperationException("Distributed write."); }
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) { if (!key.StartsWith("leistd:oauth:", StringComparison.Ordinal)) return Task.CompletedTask; Interlocked.Increment(ref Writes); throw new InvalidOperationException("Distributed write."); }
        public void Refresh(string key) => throw new InvalidOperationException("Distributed refresh.");
        public Task RefreshAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("Distributed refresh.");
        public void Remove(string key) => RemoveAsync(key).GetAwaiter().GetResult();
        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Interlocked.Increment(ref Removals);
            OnRemove?.Invoke();
            token.ThrowIfCancellationRequested();
            if (RejectRemoval || OnRemove is not null) throw new InvalidOperationException("Distributed delete.");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Hybrid_cache_never_reads_or_writes_distributed_storage_and_respects_output_expiry()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero));
        var distributed = new RejectDistributedCache();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IDistributedCache>(distributed);
        services.AddSingleton<TimeProvider>(time);
        services.AddMemoryCache(options => options.Clock = new MemoryClock(time));
        services.AddHybridCache();
        await using var provider = services.BuildServiceProvider();
        var cache = new TokenCache(provider.GetRequiredService<HybridCache>(), provider.GetRequiredService<ILogger<TokenCache>>(), time);
        var key = TokenCache.Key("test");
        var count = 0;
        ValueTask<TokenCache.Token> Fetch(CancellationToken _) => ValueTask.FromResult(new TokenCache.Token($"token-{++count}", time.GetUtcNow().AddSeconds(11)));
        Assert.Equal("token-1", (await cache.GetAsync(key, TimeSpan.FromSeconds(10), Fetch, default)).Value);
        Assert.Equal("token-1", (await cache.GetAsync(key, TimeSpan.FromSeconds(10), Fetch, default)).Value);
        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal("token-1", (await cache.GetAsync(key, TimeSpan.FromSeconds(10), Fetch, default)).Value);
        Assert.Equal(1, count);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("token-2", (await cache.GetAsync(key, TimeSpan.FromSeconds(10), Fetch, default)).Value);
        Assert.Equal(2, count);
        Assert.Equal(0, distributed.Reads);
        Assert.Equal(0, distributed.Writes);
    }
    // 两级本地缓存与 TokenCache 的 TTL 计算共享官方假时钟。
    private sealed class MemoryClock(TimeProvider time) : ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }
}
