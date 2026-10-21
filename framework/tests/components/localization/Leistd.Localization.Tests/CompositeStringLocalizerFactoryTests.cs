using Leistd.Localization.AspNetCore;
using MsOptions = Microsoft.Extensions.Options.Options;
using Leistd.Localization.Json;
using Leistd.Localization.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leistd.Localization.Tests;

/// <summary>
/// 组合工厂路由验证：仅显式登记（JsonResourceTypes）的资源类型走 JSON，其余委派官方 RESX。
/// 关键：同一程序集内 JSON marker 与 RESX marker 并存时，二者各走各的，互不误伤。
/// </summary>
public class CompositeStringLocalizerFactoryTests
{
    // 同一程序集（本测试程序集）内的两个标记类型：一个登记为 JSON，一个不登记（走 RESX）。
    private sealed class JsonResource;
    private sealed class ResxResource;

    private static CompositeStringLocalizerFactory CreateFactory()
    {
        var options = MsOptions.Create(new JsonLocalizationOptions());
        options.Value.ResourceAssemblies.Add(typeof(CompositeStringLocalizerFactoryTests).Assembly);
        // 仅把 JsonResource 精确登记为 JSON 资源类型；ResxResource 虽同程序集但未登记。
        options.Value.JsonResourceTypes.Add(typeof(JsonResource));

        var json = new JsonStringLocalizerFactory(new JsonLocalizationResourceReader(options), options);
        var fallback = new ResourceManagerStringLocalizerFactory(
            MsOptions.Create(new LocalizationOptions()),
            NullLoggerFactory.Instance);

        return new CompositeStringLocalizerFactory(json, fallback, options);
    }

    [Fact]
    public void Registered_json_type_routes_to_json_localizer()
    {
        var factory = CreateFactory();

        var localizer = factory.Create(typeof(JsonResource));

        Assert.IsType<JsonStringLocalizer>(localizer);
    }

    [Fact]
    public void Unregistered_type_in_same_assembly_falls_back_to_resource_manager()
    {
        var factory = CreateFactory();

        // ResxResource 与 JsonResource 同程序集，但未登记 → 必须走官方 RESX，不被吞进 JSON。
        var localizer = factory.Create(typeof(ResxResource));

        Assert.IsNotType<JsonStringLocalizer>(localizer);
        Assert.IsType<ResourceManagerStringLocalizer>(localizer);
    }

    [Fact]
    public void Unrelated_type_falls_back_to_resource_manager()
    {
        var factory = CreateFactory();

        var localizer = factory.Create(typeof(string));

        Assert.IsNotType<JsonStringLocalizer>(localizer);
        Assert.IsType<ResourceManagerStringLocalizer>(localizer);
    }

    [Fact]
    public void Create_by_location_always_delegates_to_resource_manager()
    {
        var factory = CreateFactory();

        // baseName/location 形态不做 JSON 猜测，一律委派官方工厂（用真实程序集名，官方工厂需 Assembly.Load 定位 RESX）。
        var assemblyName = typeof(CompositeStringLocalizerFactoryTests).Assembly.GetName().Name!;
        var localizer = factory.Create("AnyBaseName", assemblyName);

        Assert.IsNotType<JsonStringLocalizer>(localizer);
        Assert.IsType<ResourceManagerStringLocalizer>(localizer);
    }


    [Fact]
    public void Parameterless_localizer_resolves_to_json_not_resource_manager()
    {
        // 无参 IStringLocalizer 是框架全局 JSON 词条视图（业务/框架键），直取 JSON 工厂、不经组合工厂。
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddJsonLocalization()
            .BuildServiceProvider();

        var localizer = provider.GetRequiredService<IStringLocalizer>();

        Assert.IsType<JsonStringLocalizer>(localizer);
    }

    [Fact]
    public void Typed_localizer_for_unregistered_type_falls_back_to_resource_manager()
    {
        // 默认未登记任何 JsonResourceTypes → 所有 typed IStringLocalizer<T> 都走官方 RESX，宿主本地化不受接管。
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddJsonLocalization()
            .BuildServiceProvider();

        var localizer = provider.GetRequiredService<IStringLocalizer<string>>();

        Assert.IsNotType<JsonStringLocalizer>(localizer);
    }
}
