using Leistd.Tracing.AspNetCore.Middlewares;
using Leistd.Tracing.Abstractions;
using Leistd.Tracing.HttpClient.Handlers;
using Leistd.Tracing.Options;
using Leistd.Tracing.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Leistd.TestBase.Doubles;

namespace Leistd.Tracing.Tests.Core;

public sealed class CorrelationIdOptionsTests
{
    /// <summary>关闭后中间件不读请求头、不切换；进程内的 Get() 回落到 Activity（这里没有，于是为空）。</summary>
    [Fact]
    public async Task Middleware_reads_the_enabled_switch_for_each_request()
    {
        var monitor = new MutableOptionsMonitor<CorrelationIdOptions>(new());
        var provider = new CorrelationIdProvider();
        string? observed = null;
        var middleware = new CorrelationIdMiddleware(
            _ =>
            {
                observed = provider.Get();
                return Task.CompletedTask;
            },
            NullLogger<CorrelationIdMiddleware>.Instance,
            monitor);

        await middleware.InvokeAsync(ContextWithHeader("first-id"), provider);
        Assert.Equal("first-id", observed);

        monitor.Set(new CorrelationIdOptions { Enabled = false });
        await middleware.InvokeAsync(ContextWithHeader("second-id"), provider);
        Assert.Null(observed);

        monitor.Set(new CorrelationIdOptions { Enabled = true });
        await middleware.InvokeAsync(ContextWithHeader("third-id"), provider);
        Assert.Equal("third-id", observed);
    }

    [Fact]
    public async Task Outbound_handler_reads_the_enabled_switch_for_each_request()
    {
        var monitor = new MutableOptionsMonitor<CorrelationIdOptions>(new());
        var provider = new CorrelationIdProvider();
        var capture = new CapturingHttpMessageHandler();
        var handler = new CorrelationIdDelegatingHandler(provider, monitor) { InnerHandler = capture };
        using var invoker = new HttpMessageInvoker(handler);
        using var correlation = provider.Change("trace-id");

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://demo/one"), default);

        monitor.Set(new CorrelationIdOptions { Enabled = false });
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://demo/two"), default);

        monitor.Set(new CorrelationIdOptions { Enabled = true });
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://demo/three"), default);

        Assert.True(capture.Requests[0].Headers.Contains("X-Correlation-Id"));
        Assert.False(capture.Requests[1].Headers.Contains("X-Correlation-Id"));
        Assert.True(capture.Requests[2].Headers.Contains("X-Correlation-Id"));
    }

    [Fact]
    public void The_header_name_binds_from_the_configuration_section()
    {
        using var serviceProvider = BuildOptions(new Dictionary<string, string?>
        {
            ["Leistd:CorrelationId:HeaderName"] = "X-Request-Id"
        });

        var options = serviceProvider.GetRequiredService<IOptions<CorrelationIdOptions>>().Value;

        Assert.Equal("X-Request-Id", options.HeaderName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void A_blank_header_name_fails_with_its_configuration_key(string headerName)
    {
        using var serviceProvider = BuildOptions(new Dictionary<string, string?>
        {
            ["Leistd:CorrelationId:HeaderName"] = headerName
        });

        var exception = Assert.Throws<OptionsValidationException>(() =>
            serviceProvider.GetRequiredService<IOptions<CorrelationIdOptions>>().Value);
        Assert.Contains("Leistd:CorrelationId:HeaderName", exception.Message);
    }

    [Fact]
    public void The_header_name_follows_a_custom_section_path()
    {
        using var serviceProvider = BuildOptions(
            new Dictionary<string, string?> { ["Tracing:HeaderName"] = "X-Trace-Ref" },
            configSectionPath: "Tracing");

        Assert.Equal("X-Trace-Ref",
            serviceProvider.GetRequiredService<IOptions<CorrelationIdOptions>>().Value.HeaderName);
    }

    [Fact]
    public async Task Response_header_switch_uses_the_current_request_snapshot()
    {
        var monitor = new MutableOptionsMonitor<CorrelationIdOptions>(new());
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton<IOptionsMonitor<CorrelationIdOptions>>(monitor);
                    services.AddSingleton<ICorrelationIdProvider, CorrelationIdProvider>();
                })
                .Configure(app =>
                {
                    app.UseMiddleware<CorrelationIdMiddleware>();
                    app.Run(_ => Task.CompletedTask);
                }))
            .StartAsync();
        using var client = host.GetTestClient();

        using var firstRequest = new HttpRequestMessage(HttpMethod.Get, "/");
        firstRequest.Headers.Add("X-Correlation-Id", "11111111111111111111111111111111");
        using var first = await client.SendAsync(firstRequest);
        Assert.Equal("11111111111111111111111111111111", first.Headers.GetValues("X-Correlation-Id").Single());

        monitor.Set(new CorrelationIdOptions { SetResponseHeader = false });
        using var secondRequest = new HttpRequestMessage(HttpMethod.Get, "/");
        secondRequest.Headers.Add("X-Correlation-Id", "second-id");
        using var second = await client.SendAsync(secondRequest);
        Assert.False(second.Headers.Contains("X-Correlation-Id"));
    }

    private static ServiceProvider BuildOptions(
        Dictionary<string, string?> settings,
        string configSectionPath = CorrelationIdOptions.SectionName) =>
        new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
            .AddCorrelationIdCore(configSectionPath: configSectionPath)
            .BuildServiceProvider();

    private static DefaultHttpContext ContextWithHeader(string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Correlation-Id"] = value;
        return context;
    }

}
