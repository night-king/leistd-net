using System.Reflection;
using Leistd.Localization.AspNetCore;
using Leistd.Localization.Json;
using Leistd.Localization.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Localization.Tests;

/// <summary>组件自带的默认译文：登记在最前，宿主资源总能覆盖，与调用顺序无关。</summary>
/// <remarks>
/// 同一键出现在多个程序集时后登记者生效。组件的 <c>Add*</c> 常在宿主本地化注册之后调用，
/// 若按调用顺序追加，组件默认文案会静默盖掉宿主的定制——界面上看到的是"改了词条不生效"。
/// </remarks>
public class ComponentResourceRegistrationTests
{
    // 真实组件包：内嵌 Resources/en.json，带 Title:* 标题词条
    private static readonly Assembly ComponentPackage = typeof(DependencyInjection).Assembly;
    private static readonly Assembly Component = typeof(object).Assembly;
    private static readonly Assembly Host = typeof(ComponentResourceRegistrationTests).Assembly;

    /// <summary>同名键最终取到的是宿主那一份，不只是程序集顺序对。</summary>
    /// <remarks>
    /// 只断言 <c>ResourceAssemblies</c> 的下标不够：合并是在读取器里做的，真正要钉住的是"读出来的那句话"。
    /// 组件默认译文盖掉宿主定制时，表现是"改了词条不生效"，而登记顺序看上去完全正常。
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_host_text_is_what_gets_resolved(bool componentFirst)
    {
        var services = new ServiceCollection();
        if (componentFirst)
        {
            services.AddJsonLocalizationResources(ComponentPackage);
        }

        services.AddJsonLocalization(configure: options => options.ResourceAssemblies.Add(Host));

        if (!componentFirst)
        {
            services.AddJsonLocalizationResources(ComponentPackage);
        }

        using var provider = services.BuildServiceProvider();
        var texts = provider.GetRequiredService<JsonLocalizationResourceReader>().GetTexts("en");

        Assert.Equal("Host wording wins.", texts["Title:400"]);
        // 宿主没覆盖的键仍然来自组件
        Assert.Equal("Internal Server Error", texts["Title:500"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Component_resources_rank_below_the_host_regardless_of_call_order(bool componentFirst)
    {
        var services = new ServiceCollection();
        if (componentFirst)
        {
            services.AddJsonLocalizationResources(Component);
        }

        services.AddJsonLocalization(configure: options => options.ResourceAssemblies.Add(Host));

        if (!componentFirst)
        {
            services.AddJsonLocalizationResources(Component);
        }

        using var provider = services.BuildServiceProvider();
        var assemblies = provider.GetRequiredService<IOptions<JsonLocalizationOptions>>().Value.ResourceAssemblies;

        Assert.True(assemblies.IndexOf(Component) < assemblies.IndexOf(Host));
    }

    [Fact]
    public void Registering_the_same_assembly_twice_keeps_one_entry()
    {
        var services = new ServiceCollection();
        services.AddJsonLocalizationResources(Component).AddJsonLocalizationResources(Component);

        using var provider = services.BuildServiceProvider();

        Assert.Single(provider.GetRequiredService<IOptions<JsonLocalizationOptions>>().Value.ResourceAssemblies, a => a == Component);
    }
}
