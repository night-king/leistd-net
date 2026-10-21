using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Leistd.ExceptionHandling.AspNetCore;
using Leistd.ExceptionHandling.Options;
using Leistd.ExceptionHandling.Descriptors;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Leistd.ExceptionHandling.Tests;

/// <summary>全局异常处理接入真实管道后的行为：注册面、映射优先级与诊断抑制。</summary>
/// <remarks>
/// <c>AddGlobalExceptionHandler</c> / <c>UseGlobalExceptionHandler</c> 是这个家族唯一的接入方式，
/// 只测 handler 本身证明不了"它有没有被挂上去"，这里经真实管道验证两者。
/// </remarks>
public class GlobalExceptionPipelineTests
{
    private static async Task<IHost> StartAsync(
        Action<IServiceCollection> configureServices,
        Action<IApplicationBuilder>? configureApp = null)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(configureServices)
                .Configure(app =>
                {
                    app.UseGlobalExceptionHandler();
                    configureApp?.Invoke(app);

                    // 终端中间件按路径分派，不引入路由：本组的被测对象是异常中间件本身，
                    // 端点路由只会给失败原因多一层来源
                    app.Run(async context =>
                    {
                        var path = context.Request.Path.Value ?? "";
                        if (path.StartsWith("/api/orders/", StringComparison.Ordinal))
                            throw new BusinessException("OrderNotFound", $"Order {path[12..]} not found.");
                        if (path == "/api/custom")
                            throw new CustomApiException("custom failure");
                        if (path == "/api/programmer-error")
                            throw new ArgumentNullException(nameof(context), "developer-only detail");

                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync("""{"ok":true}""");
                    });
                }))
            .StartAsync();

        return host;
    }

    private static Action<IServiceCollection> WithOptions(Action<GlobalExceptionOptions>? configure = null) =>
        services => services.AddGlobalExceptionHandler(o =>
        {
            o.MapCode("OrderNotFound", StatusCodes.Status404NotFound);
            configure?.Invoke(o);
        });

    [Fact]
    public async Task Business_exception_becomes_problem_details_with_its_own_status()
    {
        using var host = await StartAsync(WithOptions());

        var response = await host.GetTestClient().GetAsync("/api/orders/1001");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Host_problem_details_customization_survives_trace_id_registration(bool hostFirst)
    {
        using var host = await StartAsync(services =>
        {
            void ConfigureHost() => services.AddProblemDetails(options =>
                options.CustomizeProblemDetails = context =>
                    context.ProblemDetails.Extensions["hostMarker"] = "retained");

            if (hostFirst)
                ConfigureHost();
            services.AddGlobalExceptionHandler(_ => { });
            if (!hostFirst)
                ConfigureHost();
        });

        var response = await host.GetTestClient().GetAsync("/api/programmer-error");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("retained", problem.GetProperty("hostMarker").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Bcl_programming_exception_is_a_safe_internal_server_error()
    {
        using var host = await StartAsync(WithOptions());

        var response = await host.GetTestClient().GetAsync("/api/programmer-error");
        var content = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(content);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        // 未预期异常只有状态码语义：本地化标题 + traceId，不合成业务码、不回显异常消息
        Assert.False(problem.RootElement.TryGetProperty("code", out _));
        Assert.False(problem.RootElement.TryGetProperty("detail", out _));
        Assert.True(problem.RootElement.TryGetProperty("traceId", out _));
        Assert.DoesNotContain("developer-only detail", content);
    }

    // 注册即绑定 Leistd:GlobalException 配置节（IncludeExceptionDetails），不必把配置传进来
    [Fact]
    public async Task The_configuration_section_is_bound()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Leistd:GlobalException:IncludeExceptionDetails"] = "true",
            })
            .Build();

        using var host = await StartAsync(services => services
            .AddSingleton<IConfiguration>(configuration)
            .AddGlobalExceptionHandler());

        var response = await host.GetTestClient().GetAsync("/api/programmer-error");
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(problem.RootElement.TryGetProperty("stackTrace", out _));
    }

    // HTTP 状态属于 API 契约，只在组合根代码里声明：配置文件里写映射不生效
    [Fact]
    public void Code_status_mappings_are_not_a_configuration_entry()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Leistd:GlobalException:CodeStatusMappings:OrderNotFound"] = "409",
                ["Leistd:GlobalException:CodeMappings:0:Code"] = "OrderNotFound",
                ["Leistd:GlobalException:CodeMappings:0:StatusCode"] = "409",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddGlobalExceptionHandler();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<GlobalExceptionOptions>>().Value;

        Assert.Empty(options.CodeStatusMappings);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Host_code_mapping_overrides_component_default_in_either_order(bool hostFirst)
    {
        using var host = await StartAsync(services => services.AddGlobalExceptionHandler(options =>
        {
            if (hostFirst)
                options.MapCode("OrderNotFound", StatusCodes.Status409Conflict);
            options.MapDefaultCode("OrderNotFound", StatusCodes.Status404NotFound);
            if (!hostFirst)
                options.MapCode("OrderNotFound", StatusCodes.Status409Conflict);
        }));

        var response = await host.GetTestClient().GetAsync("/api/orders/7");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // 正常响应不得被异常中间件改写——接入位置靠前，很容易误伤成功路径。
    [Fact]
    public async Task Successful_requests_pass_through_untouched()
    {
        using var host = await StartAsync(WithOptions());

        var response = await host.GetTestClient().GetAsync("/api/ok");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public void Registration_adds_problem_details_and_the_handler()
    {
        var services = new ServiceCollection();

        services.AddGlobalExceptionHandler(_ => { });

        Assert.Contains(services, d => d.ServiceType == typeof(IExceptionHandler));
        Assert.Contains(services, d => d.ServiceType == typeof(IProblemDetailsService));
    }

    [Fact]
    public async Task Host_can_map_a_custom_exception_without_changing_the_handler()
    {
        using var host = await StartAsync(WithOptions(options =>
            options.MapException<CustomApiException>(exception => new ExceptionDescriptor(
                StatusCodes.Status409Conflict,
                "Custom:Conflict",
                exception.Message))));

        var response = await host.GetTestClient().GetAsync("/api/custom");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Custom:Conflict", problem.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Host_type_mapping_overrides_component_default_in_either_order(bool hostFirst)
    {
        using var host = await StartAsync(services => services.AddGlobalExceptionHandler(options =>
        {
            if (hostFirst)
                options.MapException<CustomApiException>(_ => new ExceptionDescriptor(409, "Host:Conflict", "Host"));
            options.MapDefaultException<CustomApiException>(_ => new ExceptionDescriptor(503, "Component:Unavailable", "Component"));
            if (!hostFirst)
                options.MapException<CustomApiException>(_ => new ExceptionDescriptor(409, "Host:Conflict", "Host"));
        }));

        var response = await host.GetTestClient().GetAsync("/api/custom");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Host:Conflict", problem.GetProperty("code").GetString());
    }

    private sealed class CustomApiException(string message) : Exception(message);
}
