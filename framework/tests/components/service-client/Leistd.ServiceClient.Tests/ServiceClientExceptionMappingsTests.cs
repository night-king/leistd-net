using Leistd.ServiceClient.Options;
using Leistd.ExceptionHandling.AspNetCore;
using Leistd.ExceptionHandling.Descriptors;
using Leistd.ExceptionHandling.Options;
using Leistd.ServiceClient.AspNetCore;
using Leistd.ServiceClient.ExceptionMappings;
using Leistd.ServiceClient.Exceptions;
using Leistd.Tracing.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Leistd.ServiceClient.Tests;

public sealed class ServiceClientExceptionMappingsTests
{
    [Theory]
    [InlineData(ServiceClientFailureKind.Configuration, 500)]
    [InlineData(ServiceClientFailureKind.Unknown, 500)]
    [InlineData(ServiceClientFailureKind.InvalidResponse, 502)]
    [InlineData(ServiceClientFailureKind.RemoteFailure, 502)]
    [InlineData(ServiceClientFailureKind.Unavailable, 503)]
    [InlineData(ServiceClientFailureKind.Timeout, 504)]
    public async Task Failure_kinds_have_safe_component_defaults(ServiceClientFailureKind kind, int status)
    {
        var (actualStatus, body) = await HandleAsync(
            new ServiceClientException("private upstream URL and body", failureKind: kind),
            ServiceClientExceptionMappings.Configure);

        Assert.Equal(status, actualStatus);
        // 上游故障只有状态码语义：不合成错误码，也不回显上游细节
        Assert.DoesNotContain("\"code\"", body);
        Assert.DoesNotContain("private upstream URL", body);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task Remote_status_is_diagnostic_and_does_not_change_the_default_bad_gateway_response(int remoteStatus)
    {
        var (actualStatus, body) = await HandleAsync(
            new RemoteServiceException("private upstream URL", remoteStatus,
                errorCode: "Remote:Private", responseBody: "private payload"),
            ServiceClientExceptionMappings.Configure);

        Assert.Equal(StatusCodes.Status502BadGateway, actualStatus);
        Assert.DoesNotContain("\"code\"", body);
        Assert.Contains("\"title\":\"Bad Gateway\"", body);
        Assert.DoesNotContain("private upstream URL", body);
        Assert.DoesNotContain("Remote:Private", body);
        Assert.DoesNotContain("private payload", body);
    }

    // 组件只映射自己的异常类型：HttpClient.Timeout 抛出的 BCL 取消异常不被认领，保持宿主的默认 500
    [Fact]
    public async Task Bcl_cancellation_is_not_claimed_by_the_component()
    {
        var (status, _) = await HandleAsync(
            new TaskCanceledException("timed out", new TimeoutException()), ServiceClientExceptionMappings.Configure);

        Assert.Equal(StatusCodes.Status500InternalServerError, status);
    }

    [Fact]
    public async Task Without_explicit_composition_the_component_does_not_change_the_host()
    {
        var (status, body) = await HandleAsync(
            new ServiceClientException("private upstream URL", failureKind: ServiceClientFailureKind.Timeout),
            _ => { });

        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.DoesNotContain("\"code\"", body);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Host_mapping_wins_regardless_of_registration_order(bool hostFirst)
    {
        var (status, body) = await HandleAsync(
            new ServiceClientException("private upstream URL", failureKind: ServiceClientFailureKind.Timeout),
            options =>
            {
                void HostOverride() => options.MapException<ServiceClientException>(_ =>
                    new ExceptionDescriptor(StatusCodes.Status501NotImplemented, "Host:Unavailable", "Host-safe message."));

                if (hostFirst)
                    HostOverride();
                ServiceClientExceptionMappings.Configure(options);
                if (!hostFirst)
                    HostOverride();
            });

        Assert.Equal(StatusCodes.Status501NotImplemented, status);
        Assert.Contains("\"code\":\"Host:Unavailable\"", body);
        Assert.DoesNotContain("private upstream URL", body);
    }

    [Fact]
    public async Task Host_can_classify_a_known_remote_contract_without_changing_component_defaults()
    {
        var (status, body) = await HandleAsync(
            new RemoteServiceException("private upstream URL", 503, responseBody: "private payload"),
            options =>
            {
                ServiceClientExceptionMappings.Configure(options);
                options.MapException<RemoteServiceException>(exception => new ExceptionDescriptor(
                    exception.RemoteStatusCode == 503
                        ? StatusCodes.Status503ServiceUnavailable
                        : StatusCodes.Status500InternalServerError,
                    "Host:UpstreamUnavailable", "The required service is unavailable."));
            });

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.Contains("\"code\":\"Host:UpstreamUnavailable\"", body);
        Assert.DoesNotContain("private", body);
    }

    [Fact]
    public async Task The_correlation_header_is_echoed_while_the_error_traceId_stays_official()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddCorrelationId();
                    services.AddGlobalExceptionHandler(ServiceClientExceptionMappings.Configure);
                })
                .Configure(app =>
                {
                    app.UseCorrelationId();
                    app.UseGlobalExceptionHandler();
                    app.Run(_ => throw new ServiceClientException("private upstream URL",
                        failureKind: ServiceClientFailureKind.Timeout));
                }))
            .StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Correlation-Id", "caller-ABC_123");
        using var response = await host.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        // 关联标识在响应头；问题详情的 traceId 是官方链路标识，不被关联标识覆盖
        Assert.Equal("caller-ABC_123", response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Contains("\"traceId\":", body);
        Assert.DoesNotContain("caller-ABC_123", body);
    }

    private static async Task<(int Status, string Body)> HandleAsync(
        Exception exception, Action<GlobalExceptionOptions> configure)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddGlobalExceptionHandler(configure);
                })
                .Configure(app =>
                {
                    app.UseGlobalExceptionHandler();
                    app.Run(_ => throw exception);
                }))
            .StartAsync();

        using var response = await host.GetTestClient().GetAsync("/");
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    // 映射在注册客户端时登记：若要宿主在自己的 ExceptionMappings 里手写一行，
    // 漏了不会有编译或启动错误，只会让 502/503/504 静默变成 500。
    // 所以这里**故意不传** ServiceClientExceptionMappings.Configure。
    [Fact]
    public async Task Client_registration_applies_the_defaults_without_host_wiring()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddHttpClient("upstream").AddServiceClientPipeline<ServiceClientOptions>("upstream");
                    services.AddGlobalExceptionHandler(_ => { });
                })
                .Configure(app =>
                {
                    app.UseGlobalExceptionHandler();
                    app.Run(_ => throw new ServiceClientException("private upstream URL",
                        failureKind: ServiceClientFailureKind.Timeout));
                }))
            .StartAsync();

        using var response = await host.GetTestClient().GetAsync("/");

        Assert.Equal(StatusCodes.Status504GatewayTimeout, (int)response.StatusCode);
    }
}
