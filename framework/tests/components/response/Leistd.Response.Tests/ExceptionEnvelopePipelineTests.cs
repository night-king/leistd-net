using System.Net;
using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using System.Text.Json;
using Leistd.ExceptionHandling;
using Leistd.ExceptionHandling.AspNetCore;
using Leistd.Response.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Leistd.Response.Tests;

public sealed class ExceptionEnvelopePipelineTests
{
    [Fact]
    public async Task Response_wrapper_replaces_only_the_exception_representation()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddGlobalExceptionHandler(options =>
                        options.MapCode("Order:NotFound", StatusCodes.Status404NotFound));
                    services.AddControllers().AddResponseWrapper();
                })
                .Configure(app =>
                {
                    app.UseGlobalExceptionHandler();
                    app.Run(_ => throw new BusinessException("Order:NotFound", "The order was not found."));
                }))
            .StartAsync();

        var response = await host.GetTestClient().GetAsync("/");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(404, body.GetProperty("code").GetInt32());
        Assert.Equal("Order:NotFound", body.GetProperty("errorCode").GetString());
        Assert.Equal("The order was not found.", body.GetProperty("message").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }

    // 状态码页与异常同走问题详情管道：启用信封的宿主不能在 404 上冒出一份 Problem Details。
    // 协议层失败只有状态码与标题，不合成业务码。
    [Fact]
    public async Task Status_code_pages_use_the_envelope()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddGlobalExceptionHandler(_ => { });
                    services.AddControllers().AddResponseWrapper();
                })
                .Configure(app =>
                {
                    app.UseGlobalExceptionHandler();
                    app.UseStatusCodePages();
                    app.Run(context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return Task.CompletedTask;
                    });
                }))
            .StartAsync();

        var response = await host.GetTestClient().GetAsync("/missing");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(404, body.GetProperty("code").GetInt32());
        Assert.False(body.TryGetProperty("errorCode", out _));
        Assert.Equal("Not Found", body.GetProperty("message").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }

    // MVC 的错误结果不经过 IProblemDetailsService（由 ProblemDetailsFactory 生成、经输出格式化器写出），
    // 启用信封时同样要换成信封，否则同一个服务在 NotFound() 上冒出一份 Problem Details
    [Theory]
    [InlineData("/api/mvc-error-probe/not-found", 404, null)]
    [InlineData("/api/mvc-error-probe/problem", 409, "custom detail")]
    public async Task Mvc_error_results_use_the_envelope(string path, int status, string? message)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddGlobalExceptionHandler(_ => { });
                    services.AddControllers()
                        .AddApplicationPart(typeof(MvcErrorProbeController).Assembly)
                        .AddResponseWrapper();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                }))
            .StartAsync();

        var response = await host.GetTestClient().GetAsync(path);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(status, (int)response.StatusCode);
        Assert.NotEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(status, body.GetProperty("code").GetInt32());
        Assert.False(body.TryGetProperty("errorCode", out _));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        if (message is not null)
            Assert.Equal(message, body.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Automatic_model_validation_uses_one_envelope_in_either_registration_order(bool wrapperFirst)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    var mvc = services.AddControllers()
                        .AddApplicationPart(typeof(ModelValidationProbeController).Assembly);
                    if (wrapperFirst)
                    {
                        mvc.AddResponseWrapper();
                        mvc.ConfigureApiValidation();
                    }
                    else
                    {
                        mvc.ConfigureApiValidation();
                        mvc.AddResponseWrapper();
                    }
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                }))
            .StartAsync();

        var response = await host.GetTestClient().PostAsJsonAsync("/api/model-validation-probe", new { });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, body.GetProperty("code").GetInt32());
        // 输入校验是协议层失败：字段错误落在 errors 里，不合成业务码
        Assert.False(body.TryGetProperty("errorCode", out _));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        Assert.Equal("name", body.GetProperty("errors")[0].GetProperty("field").GetString());
    }
}

[ApiController]
[Route("api/model-validation-probe")]
public sealed class ModelValidationProbeController : ControllerBase
{
    [HttpPost]
    public IActionResult Post(ModelValidationProbeInput input) => Ok(input);
}

public sealed class ModelValidationProbeInput
{
    [Required]
    public string? Name { get; init; }
}

[ApiController]
[Route("api/mvc-error-probe")]
public sealed class MvcErrorProbeController : ControllerBase
{
    [HttpGet("not-found")]
    public IActionResult Missing() => NotFound();

    [HttpGet("problem")]
    public IActionResult Conflicted() => Problem(statusCode: StatusCodes.Status409Conflict, detail: "custom detail");
}
