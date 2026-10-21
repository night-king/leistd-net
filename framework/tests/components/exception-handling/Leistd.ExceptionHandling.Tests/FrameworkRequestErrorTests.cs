using System.Net;
using System.Text;
using System.Text.Json;
using Leistd.ExceptionHandling.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Leistd.ExceptionHandling.Tests;

/// <summary>框架判定的请求错误与无响应体的错误状态码走 ASP.NET Core 自己的问题详情管道，两种 <c>ThrowOnBadRequest</c> 取值同形。</summary>
/// <remarks>
/// <c>ThrowOnBadRequest</c> 默认只在开发环境开启：开发环境抛 <c>BadHttpRequestException</c>，处理器放行后由
/// 异常中间件按其状态码写出；生产环境只写状态码，由状态码页写出。两条路径都经 <c>IProblemDetailsService</c>，
/// 应得到同一个状态码与同一种响应体。这类协议层失败只有状态码与标题，不合成业务码。
/// </remarks>
public class FrameworkRequestErrorTests
{
    private static async Task<IHost> StartAsync(bool throwOnBadRequest, bool useStatusCodePages = true)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddGlobalExceptionHandler(_ => { });
                    services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = throwOnBadRequest);
                })
                .Configure(app =>
                {
                    app.UseGlobalExceptionHandler();
                    if (useStatusCodePages)
                    {
                        app.UseWhen(
                            context => context.Request.Path.StartsWithSegments("/api"),
                            api => api.UseStatusCodePages());
                    }

                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/api/items", (Item item) => Results.Ok(item));
                        endpoints.MapGet("/api/conflict", () => Results.Text("custom body", statusCode: StatusCodes.Status409Conflict));
                        endpoints.MapGet("/page/missing", () => Results.NotFound());
                    });
                }))
            .StartAsync();
        return host;
    }

    private static Task<HttpResponseMessage> PostAsync(IHost host, string body, string contentType = "application/json")
        => host.GetTestClient().PostAsync("/api/items", new StringContent(body, Encoding.UTF8, contentType));

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    [Theory]
    [InlineData(true, """{"name":""")]
    [InlineData(false, """{"name":""")]
    [InlineData(true, """{"name":123}""")]
    [InlineData(false, """{"name":123}""")]
    public async Task Unreadable_json_body_is_a_bad_request_in_both_modes(bool throwOnBadRequest, string body)
    {
        using var host = await StartAsync(throwOnBadRequest);

        using var response = await PostAsync(host, body);
        var raw = await response.Content.ReadAsStringAsync();
        var problem = await ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        // 协议层失败不合成业务码；框架的原始消息含参数名与 DTO 类型名，只进日志
        Assert.False(problem.TryGetProperty("code", out _));
        Assert.DoesNotContain(nameof(Item), raw);
        Assert.DoesNotContain("Failed to read", raw);
    }

    // 不接状态码页时，开发环境路径仍按异常自带状态码写出，不报 500
    [Fact]
    public async Task Bad_request_exception_keeps_its_status_without_status_code_pages()
    {
        using var host = await StartAsync(throwOnBadRequest: true, useStatusCodePages: false);

        using var response = await PostAsync(host, """{"name":""");
        var problem = await ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Wrong_content_type_is_unsupported_media_type(bool throwOnBadRequest)
    {
        using var host = await StartAsync(throwOnBadRequest);

        using var response = await PostAsync(host, "x", "text/plain");
        var problem = await ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("Unsupported Media Type", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Unmatched_api_route_gets_a_not_found_problem()
    {
        using var host = await StartAsync(throwOnBadRequest: false);

        using var response = await host.GetTestClient().GetAsync("/api/does-not-exist");
        var problem = await ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Not Found", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Response_with_a_body_is_left_untouched()
    {
        using var host = await StartAsync(throwOnBadRequest: false);

        using var response = await host.GetTestClient().GetAsync("/api/conflict");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("custom body", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Paths_outside_the_chosen_branch_keep_the_framework_default()
    {
        using var host = await StartAsync(throwOnBadRequest: false);

        using var response = await host.GetTestClient().GetAsync("/page/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    private sealed record Item(string Name);
}
