#if (RemoteTokenAuth)
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Leistd.Security.OpenIddict.Validation.SigningKeys;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
#if (!ResourceBrowserSession)
using Microsoft.AspNetCore.Http;
using Leistd.ServiceClient.Abstractions;
#endif
#if (ResourceBrowserSession)
using CompanyName.ProjectName.Application.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
#endif
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Validation.SystemNetHttp;
using OpenIddict.Validation;
using System.Collections.Concurrent;
using System.Diagnostics;
#if (ResourceBrowserSession)
using System.Text.RegularExpressions;
#endif

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>Identity 轮换签名证书后，资源服务的四条验签链路在第一个请求就认得新公钥；伪造 kid 的请求不能把抓取放大。</summary>
/// <remarks>
/// 两个配置管理器都是真实的（OpenIddict 验证、ASP.NET Core OIDC 处理器），只把它们的 HTTP 指向签发方替身，
/// 替身按路径计数，断言落在真实抓取次数上。
/// </remarks>
public sealed class SigningKeyRotationTests(SigningKeyRotationTests.Baseline baseline) : IClassFixture<SigningKeyRotationTests.Baseline>
{
    /// <summary>
    /// 本类共用的基准宿主（一份克隆库）。每个用例仍从它派生自己的宿主：配置管理器与刷新状态随派生宿主的服务容器各自独立，
    /// 预热过的管理器不会在用例之间共享，轮换反例不失真。
    /// </summary>
    public sealed class Baseline : IDisposable
    {
        public ProjectWebApplicationFactory Factory { get; } = new() { UseProductionAuthentication = true };

        public void Dispose() => Factory.Dispose();
    }

    private const string UpdateConfigAsBlocking = "Switch.Microsoft.IdentityModel.UpdateConfigAsBlocking";

    [Fact]
    public void The_blocking_refresh_switch_is_on_in_the_resource_host_and_in_this_test_process()
    {
        Assert.True(AppContext.TryGetSwitch(UpdateConfigAsBlocking, out var enabled) && enabled);
        var hostConfig = Path.Combine(AppContext.BaseDirectory, typeof(Program).Assembly.GetName().Name + ".runtimeconfig.json");
        using var document = JsonDocument.Parse(File.ReadAllText(hostConfig));
        Assert.True(document.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties")
            .GetProperty(UpdateConfigAsBlocking).GetBoolean());
    }

#if (!ResourceBrowserSession)
    // 在真实认证管道内调用宿主注册的读取器，失败 Bearer 不能作为下游用户令牌返回。
    [Theory]
    [InlineData("malformed")]
    [InlineData("invalid-signature")]
    public async Task Pure_api_user_access_token_requires_successful_bearer_authentication(string kind)
    {
        using var issuer = new RotatingIssuer();
        using var host = Host(baseline.Factory, issuer).WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddSingleton<IStartupFilter, AccessTokenProbe>()));
        using var client = Client(host);
        var valid = issuer.AccessToken(issuer.Current);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, valid));

        using var forgedRsa = RSA.Create(2048);
        var rejected = kind == "malformed" ? "rejected-token" :
            issuer.AccessToken(new RsaSecurityKey(forgedRsa) { KeyId = issuer.Current.KeyId });
        using var validRequest = new HttpRequestMessage(HttpMethod.Get, AccessTokenProbe.Path);
        validRequest.Headers.Authorization = new("Bearer", valid);
        using var validResponse = await client.SendAsync(validRequest);
        Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
        Assert.Equal(valid, await validResponse.Content.ReadAsStringAsync());

        using var rejectedRequest = new HttpRequestMessage(HttpMethod.Get, AccessTokenProbe.Path);
        rejectedRequest.Headers.Authorization = new("Bearer", rejected);
        using var rejectedResponse = await client.SendAsync(rejectedRequest);
        Assert.Equal(HttpStatusCode.NoContent, rejectedResponse.StatusCode);
        Assert.Empty(await rejectedResponse.Content.ReadAsStringAsync());
    }

    private sealed class AccessTokenProbe : IStartupFilter
    {
        public const string Path = "/test/user-access-token";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map(Path, branch =>
            {
                branch.UseAuthentication();
                branch.Run(async context =>
                {
                    var token = await context.RequestServices.GetRequiredService<IUserAccessTokenAccessor>().GetAccessTokenAsync();
                    if (token is null) context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                    else await context.Response.WriteAsync(token);
                });
            });
            next(app);
        };
    }
#endif

    [Fact]
    public async Task A_bearer_token_signed_by_a_newly_published_key_is_accepted_on_the_first_request()
    {
        using var issuer = new RotatingIssuer();
        using var host = Host(baseline.Factory, issuer);
        using var client = Client(host);

        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, issuer.AccessToken(issuer.Current)));
        var fetches = issuer.KeySetRequests;

        issuer.Rotate();
        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, issuer.AccessToken(issuer.Current)));
        Assert.Equal(fetches + 1, issuer.KeySetRequests);
        // 认得的 kid 不产生任何抓取
        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, issuer.AccessToken(issuer.Previous!)));
        Assert.Equal(fetches + 1, issuer.KeySetRequests);
    }

    [Fact]
    public async Task Forged_key_identifiers_cannot_multiply_key_set_requests()
    {
        using var issuer = new RotatingIssuer();
        using var host = Host(baseline.Factory, issuer);
        using var client = Client(host);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, issuer.AccessToken(issuer.Current)));
        var fetches = issuer.KeySetRequests;
        using var forgedRsa = RSA.Create(2048);
        var forged = new RsaSecurityKey(forgedRsa) { KeyId = "forged" };
        // 官方处理器验签失败后也会请求刷新：限频装在配置管理器上才约束得到它（限频本身见下面的单元用例）
        Assert.IsType<SigningKeyRefreshThrottle>(host.Services
            .GetRequiredService<IOptionsMonitor<OpenIddictValidationOptions>>().CurrentValue.ConfigurationManager);

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => MeAsync(client, issuer.AccessToken(forged))));
        Assert.All(concurrent, status => Assert.Equal(HttpStatusCode.Unauthorized, status));
        Assert.Equal(fetches + 1, issuer.KeySetRequests);
        for (var attempt = 0; attempt < 10; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(client, issuer.AccessToken(forged)));
        Assert.Equal(fetches + 1, issuer.KeySetRequests);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, issuer.AccessToken(issuer.Current)));
    }

    [Fact]
    public async Task An_unavailable_key_set_keeps_the_known_keys_and_is_not_requested_per_request()
    {
        using var issuer = new RotatingIssuer();
        using var host = Host(baseline.Factory, issuer, fetchTimeout: TimeSpan.FromMilliseconds(300));
        using var client = Client(host);
        client.Timeout = TimeSpan.FromSeconds(5);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, issuer.AccessToken(issuer.Current)));
        var attempts = issuer.DiscoveryRequests;
        issuer.KeySetUnavailable = true;
        using var forgedRsa = RSA.Create(2048);
        var forged = new RsaSecurityKey(forgedRsa) { KeyId = "forged" };

        for (var attempt = 0; attempt < 10; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(client, issuer.AccessToken(forged)));

        // 一次刷新尝试（发现文档 + JWKS，保留官方重试管道；短超时不验证重试次数），而不是每个请求一次
        Assert.Equal(attempts + 1, issuer.DiscoveryRequests);
        // 旧配置继续可用：合法令牌照常通过
        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, issuer.AccessToken(issuer.Current)));
    }

    // 签发方撤掉旧公钥：用旧私钥签、带未知 kid 的令牌触发刷新后，本次验签只能用刷新得到的公钥
    // （官方默认会尝试全部候选公钥，若保留旧动态公钥，撤掉的那张仍能验过）
    [Fact]
    public async Task A_key_revoked_at_the_issuer_no_longer_validates_after_a_refresh()
    {
        using var issuer = new RotatingIssuer();
        using var host = Host(baseline.Factory, issuer);
        using var client = Client(host);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, issuer.AccessToken(issuer.Current)));

        issuer.Rotate();
        issuer.PublishPrevious = false;
        var revoked = new RsaSecurityKey(issuer.Previous!.Rsa) { KeyId = "unknown-to-everyone" };

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(client, issuer.AccessToken(revoked)));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, issuer.AccessToken(issuer.Current)));
    }

    // 伪造 kid 的请求不论多少，刷新相关日志（Information 及以上）只有真正转交的那一次刷新
    [Fact]
    public async Task Forged_key_identifiers_do_not_multiply_log_entries()
    {
        using var issuer = new RotatingIssuer();
        using var host = Host(baseline.Factory, issuer, logs: true);
        using var client = Client(host);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, issuer.AccessToken(issuer.Current)));
        using var forgedRsa = RSA.Create(2048);
        var forged = new RsaSecurityKey(forgedRsa) { KeyId = "forged" };

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => MeAsync(client, issuer.AccessToken(forged))));
        for (var attempt = 0; attempt < 10; attempt++) await MeAsync(client, issuer.AccessToken(forged));

        Assert.Single(host.Services.GetFakeLogCollector().GetSnapshot(),
            record => record.Level >= LogLevel.Information && record.Category?.Contains("SigningKey", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task The_production_fetch_client_keeps_the_ten_second_timeout()
    {
        using var issuer = new RotatingIssuer();
        var timeouts = new ConcurrentBag<TimeSpan>();
        using var host = Host(baseline.Factory, issuer, observeClient: client => timeouts.Add(client.Timeout));
        using var client = Client(host);

        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, issuer.AccessToken(issuer.Current)));
        var expected = TimeSpan.FromSeconds(10);
        Assert.Equal(expected, host.Services.GetRequiredService<IOptions<SigningKeyRefreshOptions>>().Value.FetchTimeout);
        Assert.NotEmpty(timeouts);
        // 观察官方 SDK 实际创建的客户端，在生产配置完成后记录有效值，不猜动态客户端名称。
        Assert.All(timeouts, timeout => Assert.Equal(expected, timeout));
    }

    // JWKS 迟迟不返回：请求等到抓取超时为止，按原有公钥判定，不无限挂起
    [Fact]
    public async Task A_slow_key_set_is_bounded_by_the_fetch_timeout()
    {
        using var issuer = new RotatingIssuer();
        using var host = Host(baseline.Factory, issuer);
        using var client = Client(host);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, issuer.AccessToken(issuer.Current)));
        issuer.KeySetDelay = host.Services.GetRequiredService<IOptions<SigningKeyRefreshOptions>>().Value.FetchTimeout * 3;
        using var forgedRsa = RSA.Create(2048);
        var forged = new RsaSecurityKey(forgedRsa) { KeyId = "forged" };

        var watch = Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(client, issuer.AccessToken(forged)));
        Assert.True(watch.Elapsed < host.Services.GetRequiredService<IOptions<SigningKeyRefreshOptions>>().Value.FetchTimeout * 2, $"waited {watch.Elapsed}");

        issuer.KeySetDelay = TimeSpan.Zero;
        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, issuer.AccessToken(issuer.Current)));
    }

    // 一个等待者取消，只放弃它自己：共享的那次抓取照常完成，其他等待者拿到结果，抓取不因取消多发
    [Fact]
    public async Task A_cancelled_waiter_does_not_abandon_the_shared_fetch()
    {
        using var issuer = new RotatingIssuer();
        using var host = Host(baseline.Factory, issuer);
        using var client = Client(host);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, issuer.AccessToken(issuer.Current)));
        var fetches = issuer.KeySetRequests;
        issuer.Rotate();
        var gate = new FetchGate();
        issuer.Gate = gate;
        var token = issuer.AccessToken(issuer.Current);
        using var abandon = new CancellationTokenSource();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var cancelled = MeAsync(client, token, abandon.Token);
        Task<HttpStatusCode>? waiting = null;
        try
        {
            await gate.Started.Task.WaitAsync(watchdog.Token);
            waiting = MeAsync(client, token, watchdog.Token, "waiting");
            await issuer.WaiterEntered.Task.WaitAsync(watchdog.Token);
            Assert.False(waiting.IsCompleted, "The second waiter completed before the shared fetch was released.");
            abandon.Cancel();
            var cancellation = await Record.ExceptionAsync(() => cancelled.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(gate.TransportCancelled);
            Assert.IsAssignableFrom<OperationCanceledException>(cancellation);
            Assert.False(waiting.IsCompleted);
            gate.Release.TrySetResult();
            Assert.Equal(HttpStatusCode.OK, await waiting.WaitAsync(watchdog.Token));
            Assert.Equal(fetches + 1, issuer.KeySetRequests);
            Assert.False(gate.TransportCancelled);
        }
        finally
        {
            gate.Release.TrySetResult();
            abandon.Cancel();
            watchdog.Cancel();
            try { await cancelled.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            if (waiting is not null)
                try { await waiting.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("encrypted")]
    [InlineData("no-kid")]
    [InlineData("garbage")]
    public async Task Tokens_outside_the_handler_contract_trigger_no_key_set_request(string kind)
    {
        using var issuer = new RotatingIssuer();
        using var host = Host(baseline.Factory, issuer);
        using var client = Client(host);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, issuer.AccessToken(issuer.Current)));
        var fetches = issuer.KeySetRequests;
        using var unknownRsa = RSA.Create(2048);
        var unknown = new RsaSecurityKey(unknownRsa) { KeyId = "unknown" };
        var token = kind switch
        {
            // JWE 外层的 kid 是解密密钥，不能当签名 kid 去刷新
            "encrypted" => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = RotatingIssuer.Address, Audience = "resource-api", Claims = new Dictionary<string, object> { ["sub"] = "x" },
                SigningCredentials = new(issuer.Current, SecurityAlgorithms.RsaSha256),
                EncryptingCredentials = new(unknown, SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512)
            }),
            "no-kid" => issuer.AccessToken(new RsaSecurityKey(unknown.Rsa)),
            _ => "eyJhbGciOiJSUzI1NiIsImtpZCI6Im5ldyJ9.not-json.c2ln"
        };

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(client, token));
        Assert.Equal(fetches, issuer.KeySetRequests);
    }

#if (!SpaFrontend && (IncludeNotifications || IncludeRealTime))
    // 纯资源 API 的浏览器客户端连 Hub 只能把 Bearer 放进查询串：只在 Hub 端点上采信，普通 API 仍只认请求头
    [Fact]
    public async Task A_query_string_bearer_is_accepted_on_the_hub_only()
    {
        using var issuer = new RotatingIssuer();
        using var host = Host(baseline.Factory, issuer);
        using var client = Client(host);
        var token = issuer.AccessToken(issuer.Current);
        var negotiate = ProjectWebApplicationFactory.HubPath + "/negotiate?negotiateVersion=1";

        using (var anonymous = await client.PostAsync(negotiate, null))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var withQueryToken = await client.PostAsync($"{negotiate}&access_token={Uri.EscapeDataString(token)}", null))
            Assert.Equal(HttpStatusCode.OK, withQueryToken.StatusCode);
        using (var api = await client.GetAsync($"/api/v1/auth/me?access_token={Uri.EscapeDataString(token)}"))
            Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, token));
    }

#endif
#if (ResourceBrowserSession)
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_sign_in_after_rotation_validates_the_new_identity_and_access_tokens(bool onlyIdentityToken)
    {
        using var issuer = new RotatingIssuer();
        using var host = Host(baseline.Factory, issuer);
        Assert.NotNull(await LoginAsync(host, issuer));

        issuer.Rotate();
        // 只有 id_token 用新公钥：通过与否只取决于 OIDC 处理器那条链路（IdentityModel 的刷新重试 + 阻塞开关）
        issuer.AccessTokensWithPreviousKey = onlyIdentityToken;

        // 登录回调：id_token 走 OIDC 处理器的配置链路，访问令牌走 OpenIddict 验证链路
        var cookie = await LoginAsync(host, issuer);
        using var browser = Client(host, cookie);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_server_side_renewal_after_rotation_accepts_the_new_access_token()
    {
        using var issuer = new RotatingIssuer();
        using var host = Host(baseline.Factory, issuer);
        var cookie = await LoginAsync(host, issuer);
        var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthenticationSchemeNames.SessionCookie);
        var key = options.TicketDataFormat.Unprotect(cookie.Split('=', 2)[1])!.Principal.Claims.Single().Value;
        var ticket = (await options.SessionStore!.RetrieveAsync(key))!;
        ticket.Properties.UpdateTokenValue("expires_at", TimeProvider.System.GetUtcNow().AddSeconds(-1).ToString("o"));
        await options.SessionStore.RenewAsync(key, ticket);

        issuer.Rotate();

        using var browser = Client(host, cookie);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(1, issuer.RefreshCount);
    }
#endif

    private static async Task<HttpStatusCode> MeAsync(HttpClient client, string token, CancellationToken cancellationToken = default, string? marker = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Authorization = new("Bearer", token);
        if (marker is not null) request.Headers.Add("X-Test-Waiter", marker);
        using var response = await client.SendAsync(request, cancellationToken);
        return response.StatusCode;
    }

    private static WebApplicationFactory<Program> Host(ProjectWebApplicationFactory factory, RotatingIssuer issuer, bool logs = false,
        TimeSpan? fetchTimeout = null, Action<HttpClient>? observeClient = null) =>
        factory.WithWebHostBuilder(builder => builder.UseSetting("Authentication:Audience", "resource-api").ConfigureTestServices(services =>
        {
            // 两条链路都用真实的配置管理器：不给静态配置，只替换它们的 HTTP
#if (ResourceBrowserSession)
            services.Configure<OpenIdConnectOptions>(AuthenticationSchemeNames.OpenIdConnect, options => options.BackchannelHttpHandler = issuer);
#endif
            services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new ValidationTransport(issuer));
            services.AddSingleton<IStartupFilter>(new RequestEntry(issuer.WaiterEntered));
            if (fetchTimeout is not null || observeClient is not null)
                services.PostConfigure<OpenIddictValidationSystemNetHttpOptions>(options => options.HttpClientActions.Add(client =>
                {
                    if (fetchTimeout is { } timeout) client.Timeout = timeout;
                    observeClient?.Invoke(client);
                }));
            if (logs)
            {
                // 宿主用 Serilog 接管了日志工厂，换回标准工厂才收得到（只影响这个派生宿主）
                services.RemoveAll<ILoggerFactory>();
                services.AddLogging(logging => logging.AddFakeLogging().SetMinimumLevel(LogLevel.Information));
            }
        }));

    private static HttpClient Client(WebApplicationFactory<Program> host, string? cookie = null)
    {
        var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        client.BaseAddress = new Uri("https://localhost");
        if (cookie is not null) client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }
#if (ResourceBrowserSession)

    private static async Task<string> LoginAsync(WebApplicationFactory<Program> host, RotatingIssuer issuer)
    {
        using var browser = Client(host);
        using var challenge = await browser.GetAsync("/api/v1/auth/login?returnUrl=/workspace");
        Assert.Equal(HttpStatusCode.OK, challenge.StatusCode);
        var html = await challenge.Content.ReadAsStringAsync();
        string Field(string name) => WebUtility.HtmlDecode(Regex.Match(html,
            $"<input type=\"hidden\" name=\"{name}\" value=\"([^\"]*)\"").Groups[1].Value);
        issuer.Nonce = Field("nonce");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/signin")
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "test-code", ["state"] = Field("state") }) };
        request.Headers.Add("Cookie", string.Join("; ", challenge.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0])));
        using var callback = await browser.SendAsync(request);
        Assert.True(callback.StatusCode == HttpStatusCode.Found, await callback.Content.ReadAsStringAsync());
        return ProjectWebApplicationFactory.AssertSessionCookieContract(callback);
    }
#endif

    // next() 已启动当前宿主的真实请求管道；只观察入口，不声称观察 SDK 内部加入等待者。
    private sealed class RequestEntry(TaskCompletionSource entered) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextRequest) =>
            {
                var pending = nextRequest();
                if (context.Request.Headers["X-Test-Waiter"] == "waiting") entered.TrySetResult();
                await pending;
            });
            next(app);
        };
    }

    private sealed class FetchGate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int cancelled;
        public bool TransportCancelled => Volatile.Read(ref cancelled) != 0;

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => Interlocked.Exchange(ref cancelled, 1));
            try
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            finally
            {
                // WaitAsync 的取消续体可能先释放登记，仍须记录真实传输令牌的最终取消状态。
                if (cancellationToken.IsCancellationRequested) Interlocked.Exchange(ref cancelled, 1);
            }
        }
    }

    // OpenIddict 验证的 HTTP 客户端指向签发方替身
    private sealed class ValidationTransport(RotatingIssuer issuer) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            if (builder.Name?.StartsWith("OpenIddict.Validation.SystemNetHttp", StringComparison.Ordinal) != true) return;
            builder.PrimaryHandler = new ForwardingHandler(issuer);
        };
    }

    private sealed class ForwardingHandler(RotatingIssuer issuer) : HttpClientHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            issuer.HandleAsync(request, cancellationToken);
    }

    /// <summary>发布当前与上一张签名公钥的签发方替身：发现文档、JWKS 与令牌端点。</summary>
    private sealed class RotatingIssuer : HttpMessageHandler
    {
        public const string Address = "https://identity.test/";
        private int _keySetRequests;
        private int _discoveryRequests;
        public RsaSecurityKey Current { get; private set; } = NewKey("key-1");
        public RsaSecurityKey? Previous { get; private set; }
        public string Subject { get; } = Guid.NewGuid().ToString();
        public string Nonce { get; set; } = "";
        public int KeySetRequests => Volatile.Read(ref _keySetRequests);
        public int DiscoveryRequests => Volatile.Read(ref _discoveryRequests);
        public int RefreshCount { get; private set; }
        public bool KeySetUnavailable { get; set; }
        public bool AccessTokensWithPreviousKey { get; set; }
        /// <summary>为 false 时 JWKS 不再发布上一张公钥（撤钥），它的私钥仍可用来构造反例令牌。</summary>
        public bool PublishPrevious { get; set; } = true;
        /// <summary>JWKS 响应前的延迟，用于验证抓取超时。</summary>
        public TimeSpan KeySetDelay { get; set; }
        public FetchGate? Gate { get; set; }
        public TaskCompletionSource WaiterEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static RsaSecurityKey NewKey(string id) => new(RSA.Create(2048)) { KeyId = id };

        /// <summary>重叠轮换：新公钥加入 JWKS 并开始签名，旧公钥仍然发布。</summary>
        public void Rotate()
        {
            Previous?.Rsa.Dispose();
            Previous = Current;
            Current = NewKey($"key-{Guid.NewGuid():N}");
        }

        // 用户名随主体变化：同一测试类共用一个库，固定用户名会让后来的主体投影撞 (TenantId, Username) 唯一索引，
        // 没有成员行的主体在需要身份的端点上被拒（403 User:LocalMemberMissing）
        public string AccessToken(RsaSecurityKey key) => Token(key, "resource-api", "at+jwt", new()
        { ["sub"] = Subject, ["preferred_username"] = $"rotating-{Subject}", ["jti"] = Guid.NewGuid().ToString() });

        private static string Token(RsaSecurityKey key, string audience, string type, Dictionary<string, object> claims) =>
            new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Address, Audience = audience, Claims = claims, TokenType = type,
                IssuedAt = TimeProvider.System.GetUtcNow().UtcDateTime, NotBefore = TimeProvider.System.GetUtcNow().AddSeconds(-5).UtcDateTime,
                Expires = TimeProvider.System.GetUtcNow().AddMinutes(10).UtcDateTime, SigningCredentials = new(key, SecurityAlgorithms.RsaSha256)
            });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            HandleAsync(request, cancellationToken);

        public async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/.well-known/openid-configuration":
                    Interlocked.Increment(ref _discoveryRequests);
                    return Json(new
                    {
                        issuer = Address, authorization_endpoint = Address + "authorize", token_endpoint = Address + "token",
                        end_session_endpoint = Address + "logout", jwks_uri = Address + "jwks",
                        response_types_supported = new[] { "code" }, subject_types_supported = new[] { "public" },
                        id_token_signing_alg_values_supported = new[] { "RS256" }
                    });
                case "/jwks":
                    Interlocked.Increment(ref _keySetRequests);
                    if (Gate is { } gate) await gate.WaitAsync(cancellationToken);
                    if (KeySetDelay > TimeSpan.Zero) await Task.Delay(KeySetDelay, cancellationToken);
                    if (KeySetUnavailable) return new HttpResponseMessage(HttpStatusCode.NotFound);
                    return Json(new { keys = new[] { Current, PublishPrevious ? Previous : null }.OfType<RsaSecurityKey>().Select(Jwk).ToArray() });
                case "/token":
                    var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
                    var refresh = form["grant_type"].ToString() == "refresh_token";
                    if (refresh) RefreshCount++;
                    return Json(new
                    {
                        access_token = AccessToken(AccessTokensWithPreviousKey ? Previous! : Current),
                        id_token = Token(Current, "resource-test", "JWT", new() { ["sub"] = Subject, ["nonce"] = Nonce }),
                        refresh_token = refresh ? "refresh-2" : "refresh-1", token_type = "Bearer", expires_in = 600
                    });
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static object Jwk(RsaSecurityKey key)
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(key);
            return new { kty = "RSA", use = "sig", alg = "RS256", kid = key.KeyId, n = jwk.N, e = jwk.E };
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Current.Rsa.Dispose(); Previous?.Rsa.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
#endif
