using Leistd.AmbientContext;
using Leistd.AspNetCore.SignalR.Filters;
using Leistd.AspNetCore.SignalR.Options;
using Leistd.TestBase.Assertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.AspNetCore.SignalR.Tests;

/// <summary><c>AddSignalRAmbientContext()</c> 的注册面：过滤器只挂一次，上层组件各调一遍也不叠加。</summary>
/// <remarks>
/// 通知与实时都会调基座。过滤器挂两遍时每次 Hub 调用建立两层环境上下文、复评两遍。
/// </remarks>
public sealed class SignalRAmbientContextRegistrationTests
{
    [Fact]
    public void Registration_adds_the_filter_and_the_ambient_context()
    {
        var services = new ServiceCollection().AddLogging();

        services.AddSignalRAmbientContext();

        services.AssertSingle<AmbientContextHubFilter>(ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(IAmbientContext));
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddLogging().AddSignalRAmbientContext());

    // HubOptions 的配置器不计入 AssertIdempotent：每多一条就多挂一次过滤器
    [Fact]
    public void Repeated_registration_does_not_add_another_hub_options_configurator()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSignalRAmbientContext();
        var configurators = services.Count(d => d.ServiceType == typeof(IConfigureOptions<HubOptions>));

        services.AddSignalRAmbientContext();

        Assert.Equal(configurators, services.Count(d => d.ServiceType == typeof(IConfigureOptions<HubOptions>)));
    }

    // 上层组件各自重复调用时不带委托；宿主那次给的配置不能被吞掉
    [Fact]
    public void Configuration_from_any_call_is_applied()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSignalRAmbientContext()
            .AddSignalRAmbientContext(options => options.PolicyName = "Hub")
            .AddSignalRAmbientContext()
            .BuildServiceProvider();

        Assert.Equal("Hub", provider.GetRequiredService<IOptions<HubIdentityOptions>>().Value.PolicyName);
    }
}
