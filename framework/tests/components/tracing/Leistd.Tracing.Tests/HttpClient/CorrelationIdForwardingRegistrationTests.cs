using Leistd.TestBase.Assertions;
using Leistd.TestBase.Doubles;
using Leistd.Tracing.Abstractions;
using Leistd.Tracing.HttpClient;
using Leistd.Tracing.HttpClient.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Tracing.Tests.HttpClient;

/// <summary>出站透传的注册面：<c>AddCorrelationIdForwarding()</c>。</summary>
/// <remarks>
/// 挂在 <see cref="IHttpClientBuilder"/> 上（命名客户端），而不是全局替宿主改 HttpClient——
/// 这条边界一旦被"顺手改成全局"，所有出站请求都会带上内部标识，包括发往第三方的。
/// </remarks>
public class CorrelationIdForwardingRegistrationTests
{
    /// <summary>选项默认头名，与 <c>CorrelationIdOptions.HeaderName</c> 的默认值一致。</summary>
    private const string HeaderName = "X-Correlation-Id";

    private static IServiceCollection Base() =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddCorrelationIdCore();

    [Fact]
    public void Handler_is_registered_for_resolution()
    {
        var services = Base();

        services.AddHttpClient("downstream").AddCorrelationIdForwarding();

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<CorrelationIdDelegatingHandler>());
    }

    [Fact]
    public void Registration_returns_the_same_builder_for_chaining()
    {
        var builder = Base().AddHttpClient("downstream");

        Assert.Same(builder, builder.AddCorrelationIdForwarding());
    }

    // 处理器按客户端名累加：同一客户端挂两遍，每个请求就多走一遍处理器，而描述符层面看不出来
    [Fact]
    public void Repeated_forwarding_on_one_client_installs_one_handler()
    {
        var services = Base();
        services.AddHttpClient("downstream").AddCorrelationIdForwarding();

        services.AddHttpClient("downstream").AddCorrelationIdForwarding();

        using var provider = services.BuildServiceProvider();
        Assert.Equal(1, CountForwardingHandlers(provider, "downstream"));
        Assert.Single(services, d => d.ServiceType == typeof(CorrelationIdDelegatingHandler));
    }

    // 去重按客户端名，不是全局只装一次：第二个客户端仍要有自己的处理器
    [Fact]
    public void Each_named_client_gets_its_own_handler()
    {
        var services = Base();

        services.AddHttpClient("first").AddCorrelationIdForwarding();
        services.AddHttpClient("second").AddCorrelationIdForwarding();

        using var provider = services.BuildServiceProvider();
        Assert.Equal(1, CountForwardingHandlers(provider, "first"));
        Assert.Equal(1, CountForwardingHandlers(provider, "second"));
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services =>
            services.AddHttpClient("downstream").AddCorrelationIdForwarding());

    private static int CountForwardingHandlers(IServiceProvider provider, string clientName)
    {
        var count = 0;
        HttpMessageHandler? current = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);
        while (current is DelegatingHandler delegating)
        {
            count += delegating is CorrelationIdDelegatingHandler ? 1 : 0;
            current = delegating.InnerHandler;
        }

        return count;
    }

    // 只有登记过的命名客户端会转发；没登记的客户端必须原样不带头。
    [Fact]
    public async Task Only_the_configured_named_client_forwards_the_header()
    {
        var capture = new CapturingHttpMessageHandler();
        var services = Base();
        services.AddHttpClient("traced")
            .AddCorrelationIdForwarding()
            .ConfigurePrimaryHttpMessageHandler(() => capture);
        services.AddHttpClient("plain")
            .ConfigurePrimaryHttpMessageHandler(() => capture);

        using var provider = services.BuildServiceProvider();
        var correlation = provider.GetRequiredService<ICorrelationIdProvider>();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using (correlation.Change("0af7651916cd43dd8448eb211c80319c"))
        {
            await factory.CreateClient("traced").GetAsync("http://downstream/one");
            await factory.CreateClient("plain").GetAsync("http://downstream/two");
        }

        Assert.True(capture.Requests[0].Headers.Contains(HeaderName));
        Assert.False(capture.Requests[1].Headers.Contains(HeaderName));
    }

    // 当前没有标识时不能造一个假的塞进去：下游据此关联会得到一条不存在的链路。
    [Fact]
    public async Task No_header_is_added_when_there_is_no_current_correlation_id()
    {
        var capture = new CapturingHttpMessageHandler();
        var services = Base();
        services.AddHttpClient("traced")
            .AddCorrelationIdForwarding()
            .ConfigurePrimaryHttpMessageHandler(() => capture);

        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient("traced").GetAsync("http://downstream/x");

        Assert.False(capture.Requests.Single().Headers.Contains(HeaderName));
    }
}
