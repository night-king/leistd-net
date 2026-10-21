using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Leistd.ExceptionHandling.AspNetCore;
using Leistd.Tracing.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Leistd.ExceptionHandling.Tests;

public sealed class CorrelationIdExceptionPipelineTests
{
    /// <summary>
    /// 关联标识与链路标识分开：响应头回显调用方的关联标识，问题详情的 <c>traceId</c> 是官方写出的
    /// 当前 Activity 标识，与异常日志里记下的同值。<c>traceId</c> 若被改写成关联标识，
    /// 按它去链路追踪系统里查不到。
    /// </summary>
    [Fact]
    public async Task The_error_traceId_is_the_official_activity_id_and_the_header_carries_the_correlation_id()
    {
        string? activityIdAtThrow = null;
        string? traceIdentifierAtThrow = null;
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddCorrelationId();
                    services.AddGlobalExceptionHandler();
                })
                .Configure(app =>
                {
                    app.UseCorrelationId();
                    app.Use(async (_, next) =>
                    {
                        using var activity = new Activity("request")
                            .SetIdFormat(ActivityIdFormat.W3C)
                            .Start();
                        await next();
                    });
                    app.UseGlobalExceptionHandler();
                    app.Run(context =>
                    {
                        activityIdAtThrow = Activity.Current?.Id;
                        traceIdentifierAtThrow = context.TraceIdentifier;
                        throw new InvalidOperationException("private diagnostic");
                    });
                }))
            .StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Correlation-Id", "caller-ABC_123");
        using var response = await host.GetTestClient().SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("caller-ABC_123", response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.NotEqual("caller-ABC_123", traceIdentifierAtThrow);
        Assert.False(string.IsNullOrWhiteSpace(activityIdAtThrow));
        Assert.Equal(activityIdAtThrow, body.RootElement.GetProperty("traceId").GetString());
    }
}
