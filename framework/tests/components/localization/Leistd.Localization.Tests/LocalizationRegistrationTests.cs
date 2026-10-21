using Leistd.Localization.AspNetCore;
using Leistd.Localization.Json;
using Leistd.Localization.Options;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Localization.Tests;

/// <summary><c>AddJsonLocalization</c> 的注册面：组合工厂是唯一的全局工厂，重复调用不重复登记，宿主预先注册的无参本地化器保留。</summary>
public sealed class LocalizationRegistrationTests
{
    [Fact]
    public void Registration_installs_the_json_stack_with_documented_lifetimes()
    {
        var services = new ServiceCollection().AddLogging();

        services.AddJsonLocalization();

        services.AssertSingle<JsonLocalizationResourceReader>(ServiceLifetime.Singleton);
        services.AssertSingle<JsonStringLocalizerFactory>(ServiceLifetime.Singleton);
        services.AssertSingle<ResourceManagerStringLocalizerFactory>(ServiceLifetime.Singleton);
        services.AssertSingle<IStringLocalizer>(ServiceLifetime.Transient);
        services.AssertSingle<IStringLocalizerFactory>(ServiceLifetime.Singleton);
        services.AssertImplementedBy<IStringLocalizerFactory, CompositeStringLocalizerFactory>();
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddLogging().AddJsonLocalization());

    // 验证器不计入 AssertIdempotent；登记两份时每条失败会报两遍
    [Fact]
    public void Repeated_registration_keeps_one_options_validator_and_one_warm_up_service()
    {
        var services = new ServiceCollection().AddLogging();

        services.AddJsonLocalization().AddJsonLocalization();

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<JsonLocalizationOptions>));
        Assert.Single(services, d => d.ServiceType == typeof(IHostedService));
    }

    // 组合根拆分时各处的配置按调用顺序叠加，后者覆盖前者
    [Fact]
    public void Repeated_registration_applies_each_configuration_in_order()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddJsonLocalization(options => options.SupportedCultures = ["en"])
            .AddJsonLocalization(options => options.SupportedCultures = ["zh-CN", "en"])
            .BuildServiceProvider();

        Assert.Equal("zh-CN", provider.GetRequiredService<IOptions<JsonLocalizationOptions>>().Value.DefaultCulture);
    }

    // 官方 AddLocalization 先注册时，组合工厂替换它而不是并存：并存时谁生效取决于注册顺序
    [Fact]
    public void Composite_factory_replaces_the_official_factory_registered_earlier()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddLocalization();

        services.AddJsonLocalization();

        services.AssertImplementedBy<IStringLocalizerFactory, CompositeStringLocalizerFactory>();
    }

    [Fact]
    public void Host_registered_parameterless_localizer_is_kept()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IStringLocalizer, HostLocalizer>();

        services.AddJsonLocalization();

        services.AssertResolvesTo<IStringLocalizer, HostLocalizer>();
    }

    private sealed class HostLocalizer : IStringLocalizer
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] => new(name, name);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
