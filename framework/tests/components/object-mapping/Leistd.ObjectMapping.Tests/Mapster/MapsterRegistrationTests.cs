using Leistd.ObjectMapping.Abstractions;
using Leistd.ObjectMapping.Mapster;
using Leistd.ObjectMapping.Mapster.Options;
using Leistd.ObjectMapping.Mapster.Services;
using Leistd.TestBase.Assertions;
using global::Mapster;
using MapsterMapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.ObjectMapping.Tests.Mapster;

/// <summary>映射配置用 Mapster 官方 <see cref="IRegister"/>，扫描进组件自己的 <see cref="TypeAdapterConfig"/>。</summary>
public class MapsterRegistrationTests
{
    private sealed record Order(string Code, decimal Total);
    private sealed class OrderDto
    {
        public string Code { get; set; } = "";
        public decimal Total { get; set; }
        public string Display { get; set; } = "";
    }

    private sealed record Cart(string Owner, List<Order> Orders);
    private sealed class CartDto
    {
        public string Owner { get; set; } = "";
        public List<OrderDto> Orders { get; set; } = [];
    }

    /// <summary>被扫描发现的官方注册类。</summary>
    public sealed class OrderMappings : IRegister
    {
        public void Register(TypeAdapterConfig config) =>
            config.NewConfig<Order, OrderDto>().Map(d => d.Display, s => $"{s.Code}:{s.Total}");
    }

    private static void ScanThisAssembly(MapsterOptions options) =>
        options.Configurators.Add(config => config.Scan(typeof(OrderMappings).Assembly));

    [Fact]
    public void Registers_scanned_into_the_component_config_are_applied()
    {
        using var provider = Build(ScanThisAssembly);

        var dto = provider.GetRequiredService<IObjectMapper>().Map<Order, OrderDto>(new Order("A-1", 9.5m));

        Assert.Equal("A-1:9.5", dto.Display);
    }

    /// <summary>
    /// 嵌套映射沿用同一份配置；无参 <c>Adapt&lt;T&gt;()</c> 用的是全局配置，登记的规则在那里不存在——
    /// 这就是配置里不许用无参 <c>Adapt</c> 的原因。
    /// </summary>
    [Fact]
    public void Nested_mappings_use_the_component_config_while_parameterless_adapt_does_not()
    {
        using var provider = Build(ScanThisAssembly);
        var cart = new Cart("alice", [new Order("A-1", 1m), new Order("A-2", 2m)]);

        var dto = provider.GetRequiredService<IObjectMapper>().Map<Cart, CartDto>(cart);

        Assert.Equal(["A-1:1", "A-2:2"], dto.Orders.Select(order => order.Display));
        Assert.Equal(string.Empty, cart.Orders[0].Adapt<OrderDto>().Display);
    }

    // 扫描只发生在宿主显式登记时，不藏在注册里：每次建容器遍历全部程序集类型的成本会随业务类型数增长。
    [Fact]
    public void Registration_alone_scans_nothing()
    {
        using var provider = new ServiceCollection().AddLogging()
            .AddMapsterObjectMapper()
            .BuildServiceProvider();

        Assert.Empty(provider.GetRequiredService<IOptions<MapsterOptions>>().Value.Configurators);
    }

    // ValidateMappings 把未配置的映射提前到启动期暴露；关闭时保持惰性编译。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Validate_mappings_flag_does_not_change_mapping_results(bool validate)
    {
        using var provider = Build(o =>
        {
            o.ValidateMappings = validate;
            ScanThisAssembly(o);
        });

        Assert.Equal("A-2:1", provider.GetRequiredService<IObjectMapper>()
            .Map<Order, OrderDto>(new Order("A-2", 1m)).Display);
    }

    [Fact]
    public void Registration_exposes_the_mapper_as_singleton_and_keeps_the_mapster_mapper_available()
    {
        var services = new ServiceCollection().AddLogging().AddMapsterObjectMapper();

        services.AssertSingle<IObjectMapper>(ServiceLifetime.Singleton);
        services.AssertSingle<IMapper>(ServiceLifetime.Singleton);
        services.AssertResolvesTo<IObjectMapper, MapsterObjectMapper>();
    }

    // 宿主重复调用 AddMapsterObjectMapper 是常态（组合根拆分）。
    // 不幂等会让 IObjectMapper 被建成两份单例，配置各自独立。
    [Fact]
    public void Registration_is_idempotent()
    {
        ServiceCollectionAssertions.AssertIdempotent(services =>
        {
            services.AddLogging();
            services.AddMapsterObjectMapper();
        });
    }

    // 宿主已有自己的 Mapster 实例（含全套配置）时，组件不另建一份：IObjectMapper 走宿主那份
    [Fact]
    public void Host_registered_mapster_mapper_is_kept_and_used()
    {
        var hostConfig = new TypeAdapterConfig();
        hostConfig.NewConfig<Order, OrderDto>().Map(d => d.Display, s => "host");
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IMapper>(new Mapper(hostConfig));

        services.AddMapsterObjectMapper(ScanThisAssembly);

        Assert.NotNull(services.AssertSingle<IMapper>(ServiceLifetime.Singleton).ImplementationInstance);
        using var provider = services.BuildServiceProvider();
        Assert.Equal("host", provider.GetRequiredService<IObjectMapper>().Map<Order, OrderDto>(new Order("A-3", 1m)).Display);
    }

    // 组合根拆分时各处登记的配置都要进同一份 TypeAdapterConfig
    [Fact]
    public void Repeated_registration_accumulates_configurators()
    {
        using var provider = new ServiceCollection().AddLogging()
            .AddMapsterObjectMapper(o => o.Configurators.Add(_ => { }))
            .AddMapsterObjectMapper(ScanThisAssembly)
            .BuildServiceProvider();

        Assert.Equal(2, provider.GetRequiredService<IOptions<MapsterOptions>>().Value.Configurators.Count);
        Assert.Equal("A-4:1", provider.GetRequiredService<IObjectMapper>().Map<Order, OrderDto>(new Order("A-4", 1m)).Display);
    }

    private static ServiceProvider Build(Action<MapsterOptions> configure) =>
        new ServiceCollection().AddLogging().AddMapsterObjectMapper(configure).BuildServiceProvider();
}
