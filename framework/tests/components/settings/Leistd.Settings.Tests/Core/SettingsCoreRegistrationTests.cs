using Leistd.Settings.Definitions;
using Leistd.Settings.Management;
using Leistd.Settings.Options;
using Leistd.Settings.Resolution;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Settings.Tests.Core;

/// <summary><c>AddSettingsCore</c> 的注册面：生命周期、重复调用只叠加展示约定、宿主实现保留。</summary>
public sealed class SettingsCoreRegistrationTests
{
    // 定义管理器跨请求缓存定义；读取器按请求缓存当前主体的值；写入与用例无状态
    [Fact]
    public void Registration_uses_the_documented_lifetimes()
    {
        var services = new ServiceCollection().AddSettingsCore();

        services.AssertSingle<ISettingDefinitionManager>(ServiceLifetime.Singleton);
        services.AssertImplementedBy<ISettingDefinitionManager, SettingDefinitionManager>();
        services.AssertSingle<ISettingProvider>(ServiceLifetime.Scoped);
        services.AssertImplementedBy<ISettingProvider, DefaultSettingProvider>();
        services.AssertSingle<ISettingManager>(ServiceLifetime.Transient);
        services.AssertImplementedBy<ISettingManager, DefaultSettingManager>();
        services.AssertSingle<ISettingManagementService>(ServiceLifetime.Transient);
        services.AssertImplementedBy<ISettingManagementService, SettingManagementService>();
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddSettingsCore());

    // 服务只登记一次，但每次的展示约定都要生效：后一次覆盖前一次设置的同一属性
    [Fact]
    public void Repeated_registration_applies_each_configuration_in_order()
    {
        using var provider = new ServiceCollection()
            .AddSettingsCore(options => options.DefaultGroup = "First")
            .AddSettingsCore(options => options.DefaultGroup = "Second")
            .BuildServiceProvider();

        Assert.Equal("Second", provider.GetRequiredService<IOptions<SettingManagementOptions>>().Value.DefaultGroup);
    }

    // TryAdd 是替换口：宿主先注册自己的读取器（如带二级缓存的实现）时组件不得覆盖
    [Fact]
    public void A_host_setting_provider_is_kept()
    {
        var services = new ServiceCollection();
        services.AddScoped<ISettingProvider>(_ => throw new NotSupportedException());

        services.AddSettingsCore();

        Assert.NotNull(services.AssertSingle<ISettingProvider>(ServiceLifetime.Scoped).ImplementationFactory);
    }
}
