using Leistd.ServiceClient.Handlers;
using Leistd.ServiceClient.Options;
using Leistd.ServiceClient.Tests.TestDoubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.ServiceClient.Tests.Core;

/// <summary><c>AddServiceClientPipeline</c> 的重复调用契约：同一客户端相同选项类型只挂一套处理器，换用另一选项类型在注册时报错。</summary>
/// <remarks>
/// 处理器经 <c>IConfigureOptions&lt;HttpClientFactoryOptions&gt;</c> 按客户端名累加，重复挂载不会报错，
/// 只会让每个请求多走一遍传输异常翻译与链路透传，所以从构建出的处理器链计数。
/// </remarks>
public sealed class ServiceClientPipelineRegistrationTests
{
    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        return services;
    }

    [Fact]
    public void Repeating_the_pipeline_on_one_client_keeps_one_handler_of_each_kind()
    {
        var services = Services();

        services.AddHttpClient("upstream").AddServiceClientPipeline<ServiceClientOptions>("upstream");
        services.AddHttpClient("upstream").AddServiceClientPipeline<ServiceClientOptions>("upstream");

        using var provider = services.BuildServiceProvider();
        Assert.Equal(1, HandlerChain.Count<TransportFailureHandler>(provider, "upstream"));
        Assert.Equal(1, HandlerChain.Count<PassthroughDelegatingHandler>(provider, "upstream"));
    }

    // AddServiceClient 内部已装配管道，宿主再显式补一次不应叠第二层
    [Fact]
    public void The_pipeline_after_AddServiceClient_adds_no_handler()
    {
        var services = Services();
        services.AddServiceClient<ServiceClientRegistrationTests.IOrdersClient, OrdersClientImplementation,
            ServiceClientRegistrationTests.OrdersClientOptions>("Orders");

        var builder = services.AddHttpClient("Orders")
            .AddServiceClientPipeline<ServiceClientRegistrationTests.OrdersClientOptions>("Orders");

        Assert.Equal("Orders", builder.Name);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(1, HandlerChain.Count<TransportFailureHandler>(provider, "Orders"));
        Assert.Equal(1, HandlerChain.Count<PassthroughDelegatingHandler>(provider, "Orders"));
    }

    [Fact]
    public void Different_clients_each_get_their_own_pipeline()
    {
        var services = Services();

        services.AddHttpClient("first").AddServiceClientPipeline<ServiceClientOptions>("first");
        services.AddHttpClient("second").AddServiceClientPipeline<ServiceClientOptions>("second");

        using var provider = services.BuildServiceProvider();
        Assert.Equal(1, HandlerChain.Count<TransportFailureHandler>(provider, "first"));
        Assert.Equal(1, HandlerChain.Count<TransportFailureHandler>(provider, "second"));
    }

    // 两种选项类型各配一次基址时后者静默覆盖前者，所以在注册时拒绝，且不留下第二套处理器
    [Fact]
    public void Another_options_type_on_the_same_client_is_rejected()
    {
        var services = Services();
        services.AddHttpClient("upstream").AddServiceClientPipeline<ServiceClientOptions>("upstream");

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddHttpClient("upstream")
            .AddServiceClientPipeline<ServiceClientRegistrationTests.OrdersClientOptions>("upstream"));

        Assert.Contains("'upstream'", exception.Message, StringComparison.Ordinal);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(1, HandlerChain.Count<TransportFailureHandler>(provider, "upstream"));
    }

    private sealed class OrdersClientImplementation(HttpClient http) : ServiceClientRegistrationTests.IOrdersClient
    {
        public Uri? BaseAddress => http.BaseAddress;
    }
}
