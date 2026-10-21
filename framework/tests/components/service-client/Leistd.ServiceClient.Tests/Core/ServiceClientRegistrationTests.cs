using Leistd.ServiceClient.Handlers;
using Leistd.ServiceClient.Options;
using Leistd.ServiceClient.Tests.TestDoubles;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.ServiceClient.Tests.Core;

/// <summary><c>AddServiceClient</c> 的注册面：先绑定配置节再应用委托、按服务名登记一次、冲突的登记在注册时报错。</summary>
/// <remarks>
/// 同一个命名客户端被登记两遍时，处理器管道会叠两层：每个请求走两遍传输异常翻译与链路透传，
/// 而服务集合里的描述符看起来一切正常——只有构建出的处理器链能看出来。
/// </remarks>
public sealed class ServiceClientRegistrationTests
{
    private static ServiceCollection Services(Dictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build());
        return services;
    }

    [Fact]
    public void Registration_adds_the_typed_client_and_one_standard_pipeline()
    {
        var services = Services();

        services.AddServiceClient<IOrdersClient, OrdersClient, OrdersClientOptions>("Orders");

        services.AssertSingle<IOrdersClient>(ServiceLifetime.Transient);
        using var provider = services.BuildServiceProvider();
        Assert.IsType<OrdersClient>(provider.GetRequiredService<IOrdersClient>());
        Assert.Equal(1, HandlerChain.Count<TransportFailureHandler>(provider, "Orders"));
        // 未注册关联标识时那一环是直通处理器，不是缺位
        Assert.Equal(1, HandlerChain.Count<PassthroughDelegatingHandler>(provider, "Orders"));
    }

    // 配置节给部署基线，委托在其后应用：同一键两边都给时以代码为准，其余键仍取配置
    [Fact]
    public void Configuration_is_bound_first_and_the_delegate_overrides_it()
    {
        var services = Services(new()
        {
            ["Leistd:ServiceClients:Orders:BaseAddress"] = "http://orders",
            ["Leistd:ServiceClients:Orders:Region"] = "configured",
        });

        services.AddServiceClient<IOrdersClient, OrdersClient, OrdersClientOptions>("Orders", o => o.Region = "code");

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<OrdersClientOptions>>().Value;
        Assert.Equal("http://orders", options.BaseAddress);
        Assert.Equal("code", options.Region);
    }

    [Fact]
    public void A_custom_section_path_is_bound_instead_of_the_default()
    {
        var services = Services(new()
        {
            ["Leistd:ServiceClients:Orders:BaseAddress"] = "http://default",
            ["Downstream:Orders:BaseAddress"] = "http://custom",
        });

        services.AddServiceClient<IOrdersClient, OrdersClient, OrdersClientOptions>("Orders", configSectionPath: "Downstream:Orders");

        using var provider = services.BuildServiceProvider();
        Assert.Equal("http://custom", provider.GetRequiredService<IOptions<OrdersClientOptions>>().Value.BaseAddress);
    }

    // 返回的构建器是该命名客户端的：宿主在上面追加的配置要落到同一个客户端上
    [Fact]
    public void The_returned_builder_keeps_configuring_the_named_client()
    {
        var services = Services();

        var builder = services.AddServiceClient<IOrdersClient, OrdersClient, OrdersClientOptions>("Orders")
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(7));

        Assert.Equal("Orders", builder.Name);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(TimeSpan.FromSeconds(7), provider.GetRequiredService<IHttpClientFactory>().CreateClient("Orders").Timeout);
    }

    [Fact]
    public void Repeating_the_same_registration_adds_no_service()
    {
        ServiceCollectionAssertions.AssertIdempotent(services =>
            services.AddServiceClient<IOrdersClient, OrdersClient, OrdersClientOptions>("Orders"));
    }

    [Fact]
    public void Repeating_the_same_registration_keeps_one_pipeline_and_returns_the_same_client()
    {
        var services = Services();
        services.AddServiceClient<IOrdersClient, OrdersClient, OrdersClientOptions>("Orders");

        var repeated = services.AddServiceClient<IOrdersClient, OrdersClient, OrdersClientOptions>("Orders");

        Assert.Equal("Orders", repeated.Name);
        services.AssertSingle<IOrdersClient>(ServiceLifetime.Transient);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(1, HandlerChain.Count<TransportFailureHandler>(provider, "Orders"));
        Assert.Equal(1, HandlerChain.Count<PassthroughDelegatingHandler>(provider, "Orders"));
    }

    // 组合根拆分时，后一处的委托照样生效
    [Fact]
    public void Repeating_the_same_registration_applies_the_later_delegate()
    {
        var services = Services();
        services.AddServiceClient<IOrdersClient, OrdersClient, OrdersClientOptions>("Orders", o => o.Region = "first");

        services.AddServiceClient<IOrdersClient, OrdersClient, OrdersClientOptions>("Orders", o => o.Region = "second");

        using var provider = services.BuildServiceProvider();
        Assert.Equal("second", provider.GetRequiredService<IOptions<OrdersClientOptions>>().Value.Region);
    }

    [Fact]
    public void Different_service_names_coexist()
    {
        var services = Services(new()
        {
            ["Leistd:ServiceClients:Orders:BaseAddress"] = "http://orders",
            ["Leistd:ServiceClients:Inventory:BaseAddress"] = "http://inventory",
        });

        services.AddServiceClient<IOrdersClient, OrdersClient, OrdersClientOptions>("Orders");
        services.AddServiceClient<IInventoryClient, InventoryClient, InventoryClientOptions>("Inventory");

        using var provider = services.BuildServiceProvider();
        Assert.Equal(new Uri("http://orders/"), provider.GetRequiredService<IOrdersClient>().BaseAddress);
        Assert.Equal(new Uri("http://inventory/"), provider.GetRequiredService<IInventoryClient>().BaseAddress);
        Assert.Equal(1, HandlerChain.Count<TransportFailureHandler>(provider, "Orders"));
        Assert.Equal(1, HandlerChain.Count<TransportFailureHandler>(provider, "Inventory"));
    }

    // 同名的第二种登记会让两套管道叠在一个客户端上；一个选项类型服务两个名字会让两个配置节互相覆盖。
    // 两者都不报错地"能用"，所以在注册时就拒绝。
    [Theory]
    [InlineData("another client type")]
    [InlineData("another options type")]
    [InlineData("another section")]
    [InlineData("options type reused by another name")]
    public void A_conflicting_registration_is_rejected(string conflict)
    {
        var services = Services();
        services.AddServiceClient<IOrdersClient, OrdersClient, OrdersClientOptions>("Orders");

        Action register = conflict switch
        {
            "another client type" => () => services.AddServiceClient<IInventoryClient, InventoryClient, InventoryClientOptions>("Orders"),
            "another options type" => () => services.AddServiceClient<IOrdersClient, OrdersClient, InventoryClientOptions>("Orders"),
            "another section" => () => services.AddServiceClient<IOrdersClient, OrdersClient, OrdersClientOptions>("Orders", configSectionPath: "Other"),
            _ => () => services.AddServiceClient<IInventoryClient, InventoryClient, OrdersClientOptions>("Inventory"),
        };

        Assert.Throws<InvalidOperationException>(register);
    }

    // 只换接口、实现与选项都不变：若按实现判定"相同登记"，第二次调用会被当成重复而不登记新接口，
    // 注册不报错、解析 ISecond 时才失败。必须在注册时就拒绝。
    [Fact]
    public void Another_interface_on_the_same_implementation_and_name_is_rejected()
    {
        var services = Services();
        services.AddServiceClient<IFirstClient, BothClient, OrdersClientOptions>("Orders");

        var error = Assert.Throws<InvalidOperationException>(
            () => services.AddServiceClient<ISecondClient, BothClient, OrdersClientOptions>("Orders"));

        Assert.Contains(nameof(ISecondClient), error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("")]
    public void A_blank_section_path_is_rejected(string path)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            Services().AddServiceClient<IOrdersClient, OrdersClient, OrdersClientOptions>("Orders", configSectionPath: path));
    }

    public sealed class OrdersClientOptions : ServiceClientOptions
    {
        public string? Region { get; set; }
    }

    public sealed class InventoryClientOptions : ServiceClientOptions;

    public interface IOrdersClient
    {
        Uri? BaseAddress { get; }
    }

    public interface IInventoryClient
    {
        Uri? BaseAddress { get; }
    }

    private sealed class OrdersClient(HttpClient http) : IOrdersClient
    {
        public Uri? BaseAddress => http.BaseAddress;
    }

    private sealed class InventoryClient(HttpClient http) : IInventoryClient
    {
        public Uri? BaseAddress => http.BaseAddress;
    }

    public interface IFirstClient;

    public interface ISecondClient;

    private sealed class BothClient : IFirstClient, ISecondClient;
}
