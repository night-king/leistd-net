using System.Net;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Leistd.ServiceClient.OAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using OpenIddict.Abstractions;
using OpenIddict.Client;
using OpenIddict.Client.SystemNetHttp;
using Xunit;

namespace Leistd.ServiceClient.Tests.OAuth;

// 远端响应原文（令牌端点即含 access_token）不得进入任何日志：结构化属性、渲染消息、异常文本都检查哨兵。
// 走真实 OpenIddict Client 管道与 TestServer 协议端点，只替换传输。
public sealed class ResponsePayloadLoggingTests : IAsyncLifetime
{
    private const string Sentinel = "SENTINEL-ACCESS-TOKEN-eyJhbGciOi.eyJzdWIiOiJ4In0.c2ln";
    private WebApplication host = null!;
    private Func<HttpContext, IResult> token = null!;
    // 为 null 时返回正常的发现文档与 JWKS；失败用例替换其中一个
    private Func<HttpContext, IResult>? discovery;
    private Func<HttpContext, IResult>? keySet;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        host = builder.Build();
        host.MapGet("/.well-known/openid-configuration", (HttpContext context) => discovery?.Invoke(context) ?? Results.Json(new
        {
            issuer = $"http://{context.Request.Host}/", token_endpoint = $"http://{context.Request.Host}/connect/token",
            jwks_uri = $"http://{context.Request.Host}/jwks", response_types_supported = new[] { "code" },
            subject_types_supported = new[] { "public" }, id_token_signing_alg_values_supported = new[] { "RS256" },
            token_endpoint_auth_methods_supported = new[] { "client_secret_post" },
            grant_types_supported = new[] { "client_credentials" }
        }));
        using var rsa = RSA.Create(2048);
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(rsa));
        host.MapGet("/jwks", (HttpContext context) => keySet?.Invoke(context) ??
            Results.Json(new { keys = new[] { new { kty = "RSA", e = jwk.E, n = jwk.N, use = "sig", kid = "test" } } }));
        host.MapPost("/connect/token", (HttpContext context) => token(context));
        await host.StartAsync();
    }

    public Task DisposeAsync() => host.DisposeAsync().AsTask();

    private (ServiceProvider Provider, FakeLogCollector Logs) Caller(Func<DelegatingHandler>? transport = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddFakeLogging().SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new ProtocolTransport(host.GetTestServer(), transport));
        services.AddServiceAuthentication(value =>
        {
            value.Authority = "http://localhost/"; value.ClientId = "orders-api"; value.ClientSecret = "test-secret";
        });
        var provider = services.BuildServiceProvider();
        return (provider, provider.GetFakeLogCollector());
    }

    private static Task<OpenIddictClientModels.ClientCredentialsAuthenticationResult> RequestAsync(ServiceProvider provider, CancellationToken cancellationToken = default) =>
        provider.GetRequiredService<OpenIddictClientService>().AuthenticateWithClientCredentialsAsync(new()
        {
            RegistrationId = Leistd.ServiceClient.OAuth.DependencyInjection.RegistrationId, CancellationToken = cancellationToken
        }).AsTask();

    private static void AssertNoSentinel(FakeLogCollector logs, Exception? exception = null)
    {
        foreach (var record in logs.GetSnapshot())
        {
            Assert.DoesNotContain(Sentinel, record.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Sentinel, record.Exception?.ToString() ?? "", StringComparison.Ordinal);
            foreach (var pair in record.StructuredState ?? [])
                Assert.DoesNotContain(Sentinel, pair.Value ?? "", StringComparison.Ordinal);
        }
        Assert.DoesNotContain(Sentinel, exception?.ToString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_after_the_response_arrives_propagates_without_logging_the_token_response()
    {
        token = _ => Results.Json(new { access_token = Sentinel, token_type = "Bearer", expires_in = 600 });
        using var cancellation = new CancellationTokenSource();
        var (provider, logs) = Caller(() => new CancelAfterTokenResponse(cancellation));
        await using var _ = provider;

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RequestAsync(provider, cancellation.Token));

        AssertNoSentinel(logs, exception);
        Assert.DoesNotContain(logs.GetSnapshot(), record => record.Level >= LogLevel.Error);
    }

    // 官方 6182 会把调用方取消记成网络故障并拒绝为 server_error；这里应照常抛出取消且不留错误日志
    [Fact]
    public async Task Cancellation_while_the_request_is_in_flight_propagates_without_an_error()
    {
        token = _ => Results.Json(new { access_token = "machine-1", token_type = "Bearer", expires_in = 600 });
        using var cancellation = new CancellationTokenSource();
        var (provider, logs) = Caller(() => new CancelBeforeTokenRequest(cancellation));
        await using var _ = provider;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RequestAsync(provider, cancellation.Token));

        Assert.DoesNotContain(logs.GetSnapshot(), record => record.Level >= LogLevel.Error);
    }

    // 调用方令牌有效时的传输故障（含超时，也是 OperationCanceledException）仍是远端故障：与官方一样记 6182 并以同样详情拒绝
    public static TheoryData<string, Func<Exception>> TransportFailures => new()
    {
        { "connection-refused", () => new HttpRequestException("Connection refused") },
        { "timeout", () => new TaskCanceledException("The request timed out.", new TimeoutException()) },
        // 官方 IsFatal 不把 InsufficientMemoryException 算作致命
        { "insufficient-memory", () => new HttpRequestException("Allocation failed", new InsufficientMemoryException()) }
    };

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public async Task Transport_failures_with_a_live_caller_token_are_still_rejected_and_logged(string scenario, Func<Exception> failure)
    {
        var (provider, logs) = Caller(() => new FailTokenRequest(failure));
        await using var _ = provider;

        var exception = await Assert.ThrowsAsync<OpenIddictExceptions.ProtocolException>(() => RequestAsync(provider));

        Assert.True(OpenIddictConstants.Errors.ServerError == exception.Error, $"{scenario}: got {exception.Error}");
        Assert.Equal("An error occurred while communicating with the remote HTTP server.", exception.ErrorDescription);
        Assert.Equal("https://documentation.openiddict.com/errors/ID2136", exception.ErrorUri);
        Assert.Contains(logs.GetSnapshot(), record => record.Level == LogLevel.Error && record.Id.Id == 6182 && record.Exception is not null);
    }

    // 内层是内存耗尽时与官方一样不转写成协议失败，照常向外抛
    [Fact]
    public async Task A_fatal_inner_exception_is_not_turned_into_a_protocol_failure()
    {
        // 有意构造运行时保留的异常类型，模拟传输层包装的内存耗尽
#pragma warning disable CA2201
        var (provider, _) = Caller(() => new FailTokenRequest(() => new HttpRequestException("Allocation failed", new OutOfMemoryException())));
#pragma warning restore CA2201
        await using var _ = provider;

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => RequestAsync(provider));

        Assert.IsType<OutOfMemoryException>(exception.InnerException);
    }

    [Fact]
    public async Task A_successful_token_response_is_unaffected()
    {
        token = _ => Results.Json(new { access_token = "machine-1", token_type = "Bearer", expires_in = 600 });
        var (provider, logs) = Caller();
        await using var _ = provider;

        var result = await RequestAsync(provider);

        Assert.Equal("machine-1", result.AccessToken);
        Assert.DoesNotContain(logs.GetSnapshot(), record => record.Level >= LogLevel.Error);
    }

    public static TheoryData<string, int, string?, string, string> FailingResponses => new()
    {
        // 成功状态码却取不出响应：官方事件 6185 会写原文（不支持的类型、缺 Content-Type、JSON 正文标错类型）
        { "success-text", 200, "text/plain", $"access_token={Sentinel}", OpenIddictConstants.Errors.ServerError },
        { "success-without-content-type", 200, null, $"{{\"access_token\":\"{Sentinel}\"}}", OpenIddictConstants.Errors.ServerError },
        { "success-json-labelled-html", 200, "text/html", $"{{\"access_token\":\"{Sentinel}\"}}", OpenIddictConstants.Errors.ServerError },
        // 截断的 JSON：解析失败按官方语义拒绝为 server_error。
        { "truncated-json", 200, "application/json", $"{{\"access_token\":\"{Sentinel}", OpenIddictConstants.Errors.ServerError },
        // 非成功状态、合法 JSON 但没有 OAuth error：官方会重读原文记 6184。
        { "error-status-json", 401, "application/json", $"{{\"access_token\":\"{Sentinel}\"}}", OpenIddictConstants.Errors.InvalidToken },
        // 非 JSON 的错误页里夹带数据。
        { "error-status-html", 503, "text/html", $"<html>{Sentinel}</html>", OpenIddictConstants.Errors.TemporarilyUnavailable }
    };

    [Theory]
    [MemberData(nameof(FailingResponses))]
    public async Task Failing_responses_are_rejected_with_the_official_error_but_never_logged_verbatim(
        string scenario, int status, string? contentType, string body, string expectedError)
    {
        token = _ => new RawResult(status, contentType, body);
        var (provider, logs) = Caller();
        await using var _ = provider;

        var exception = await Assert.ThrowsAsync<OpenIddictExceptions.ProtocolException>(() => RequestAsync(provider));

        Assert.True(expectedError == exception.Error, $"{scenario}: expected {expectedError}, got {exception.Error}");
        AssertNoSentinel(logs, exception);
        Assert.Contains(logs.GetSnapshot(), record => record.Level == LogLevel.Error &&
            record.Message.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    // 发现文档与 JWKS 走同一套前置处理：它们的失败响应同样不得原文落盘（用不触发官方重试退避的状态码，用例不必等待）
    [Theory]
    [InlineData("discovery", 200, "text/plain")]
    [InlineData("discovery", 400, "text/html")]
    [InlineData("jwks", 200, "text/plain")]
    [InlineData("jwks", 400, "application/json")]
    public async Task Discovery_and_key_set_failures_are_never_logged_verbatim(string endpoint, int status, string contentType)
    {
        token = _ => Results.Json(new { access_token = "machine-1", token_type = "Bearer", expires_in = 600 });
        Func<HttpContext, IResult> failing = _ => new RawResult(status, contentType, $"{{\"secret\":\"{Sentinel}\"}}");
        if (endpoint == "discovery") discovery = failing; else keySet = failing;
        var (provider, logs) = Caller();
        await using var _ = provider;

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => RequestAsync(provider));

        AssertNoSentinel(logs, exception);
        Assert.Contains(logs.GetSnapshot(), record => record.Level == LogLevel.Error &&
            record.Message.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    // 带 +json 结构化后缀的类型与官方一样按 JSON 解析：合法的错误响应照常取出 OAuth error
    [Fact]
    public async Task Structured_json_media_types_are_parsed_like_the_official_handler()
    {
        token = _ => new RawResult(400, "application/problem+json", $"{{\"error\":\"invalid_client\",\"access_token\":\"{Sentinel}\"}}");
        var (provider, logs) = Caller();
        await using var _ = provider;

        var exception = await Assert.ThrowsAsync<OpenIddictExceptions.ProtocolException>(() => RequestAsync(provider));

        Assert.Equal(OpenIddictConstants.Errors.InvalidClient, exception.Error);
        AssertNoSentinel(logs, exception);
    }

    // 原样写出状态码、Content-Type（可缺省）与正文：Results.Content 缺省时会补 text/plain
    private sealed class RawResult(int status, string? contentType, string body) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = status;
            if (contentType is not null) httpContext.Response.ContentType = contentType;
            await httpContext.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(body));
        }
    }

    // 把 OpenIddict 的 HTTP 客户端指向 TestServer；可选地在传输链上插入一个处理器，模拟取消或传输故障。
    private sealed class ProtocolTransport(TestServer server, Func<DelegatingHandler>? transport) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            if (!builder.Name!.StartsWith(typeof(OpenIddictClientSystemNetHttpOptions).Assembly.GetName().Name!, StringComparison.Ordinal)) return;
            builder.PrimaryHandler.Dispose();
            builder.PrimaryHandler = server.CreateHandler();
            if (transport is not null) builder.AdditionalHandlers.Add(transport());
        };
    }

    // 令牌请求发出前取消调用方令牌：请求在途中被放弃，发送阶段收到的是调用方自己的取消
    private sealed class CancelBeforeTokenRequest(CancellationTokenSource cancellation) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/connect/token", StringComparison.Ordinal))
                await cancellation.CancelAsync();
            return await base.SendAsync(request, cancellationToken);
        }
    }

    // 令牌请求在传输层失败，调用方令牌始终有效
    private sealed class FailTokenRequest(Func<Exception> failure) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.AbsolutePath.EndsWith("/connect/token", StringComparison.Ordinal)
                ? Task.FromException<HttpResponseMessage>(failure())
                : base.SendAsync(request, cancellationToken);
    }

    private sealed class CancelAfterTokenResponse(CancellationTokenSource cancellation) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri!.AbsolutePath.EndsWith("/connect/token", StringComparison.Ordinal))
            {
                await response.Content.LoadIntoBufferAsync(cancellationToken);
                await cancellation.CancelAsync();
            }
            return response;
        }
    }
}
