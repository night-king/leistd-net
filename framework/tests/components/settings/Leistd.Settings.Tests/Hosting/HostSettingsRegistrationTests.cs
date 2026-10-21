using Leistd.BackgroundJobs.Recurring;
using Leistd.EventBus.EventHandlers;
using Leistd.Security.Users;
using Leistd.Settings.Definitions;
using Leistd.Settings.Events;
using Leistd.Settings.Hosting;
using Leistd.Settings.Hosting.Configuration;
using Leistd.Settings.Hosting.Options;
using Leistd.Settings.Hosting.Runtime;
using Leistd.Settings.Stores;
using Leistd.Settings.Tests.TestDoubles;
using Leistd.TestBase.Assertions;
using Leistd.TestBase.Doubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;
using Leistd.Lock.Memory;
using Leistd.UnitOfWork;

namespace Leistd.Settings.Tests.Hosting;

/// <summary>
/// <c>AddHostSettings</c> 的注册面：服务与刷新任务只登记一次；相同绑定不重复生效、不同设置累加、
/// 同名不同绑定在登记时拒绝；刷新周期按实际配置节校验。
/// </summary>
public sealed class HostSettingsRegistrationTests
{
    private static IServiceCollection Base(IDictionary<string, string?>? settings = null) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build());

    private static void Bind(HostSettingBindingBuilder bindings) => bindings.Bind("Demo.Level", ["Demo:Level"]);

    private static void BindAll(HostSettingBindingBuilder bindings) => bindings
        .Bind("Demo.Level", ["Demo:Level"], fallback: "Information")
        .BindOption<DemoOptions>("Demo.Days", "Demo", nameof(DemoOptions.Days));

    private static IEnumerable<string> BoundNames(IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<HostSettingBindingCollection>>().Value.Bindings.Select(binding => binding.SettingName);

    private static async Task<IHost> StartAsync(FakeSettingStore store, params Action<HostSettingBindingBuilder>[] declarations)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:Level"] = "Warning", ["Demo:Days"] = "90" });
        builder.Services
            .AddSettingsCore()
            .AddSingleton<ISettingStore>(store)
            .AddMemoryLocalLock()
            .AddSingleton<IUnitOfWorkManager>(new RecordingUnitOfWorkManager())
            .AddSingleton<ICurrentUser>(new FakeCurrentUser())
            .AddSingleton<ISettingDefinitionProvider, DemoDefinitions>();
        foreach (var declaration in declarations)
        {
            builder.Services.AddHostSettings(declaration);
        }

        var host = builder.Build().UseHostSettings();
        await host.StartAsync();
        return host;
    }

    [Fact]
    public void Registration_exposes_the_configuration_source_applier_and_refresh_job()
    {
        var services = Base().AddHostSettings(Bind);

        services.AssertSingle<HostSettingsConfigurationProvider>(ServiceLifetime.Singleton);
        services.AssertSingle<HostSettingApplier>(ServiceLifetime.Scoped);
        Assert.Single(services, d => d.ServiceType == typeof(IEventHandler<SettingChangedEvent>));
        Assert.Single(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(HostSettingStartupService));
        Assert.Single(services.Select(d => d.ImplementationInstance).OfType<RecurringJobDefinition>());
    }

    // AssertIdempotent 不看验证器与选项配置：另外核对验证器、刷新任务与托管服务不叠加，
    // 并解析绑定、启动宿主，确认重复调用在消费端也不出错
    [Fact]
    public async Task Identical_registrations_take_effect_once()
    {
        ServiceCollectionAssertions.AssertIdempotent(services => services.AddHostSettings(BindAll));

        var services = Base().AddHostSettings(BindAll).AddHostSettings(BindAll);

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<HostSettingOptions>));
        Assert.Single(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(HostSettingStartupService));
        Assert.Single(services.Select(d => d.ImplementationInstance).OfType<RecurringJobDefinition>());
        // 绑定在登记时并入单例，没有会在解析时再执行一遍绑定声明的选项配置回调
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IConfigureOptions<HostSettingBindingCollection>));
        using (var provider = services.BuildServiceProvider())
        {
            Assert.Equal(["Demo.Level", "Demo.Days"], BoundNames(provider));
        }

        var store = new FakeSettingStore();
        store.Host["Demo.Level"] = "Debug";
        store.Host["Demo.Days"] = "120";
        using var host = await StartAsync(store, BindAll, BindAll);

        Assert.Equal(["Demo.Level", "Demo.Days"], BoundNames(host.Services));
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        Assert.Equal(("Debug", "120"), (configuration["Demo:Level"], configuration["Demo:Days"]));
    }

    [Fact]
    public void Identical_bindings_within_one_declaration_take_effect_once()
    {
        using var provider = Base()
            .AddHostSettings(bindings => bindings.Bind("Demo.Level", ["Demo:Level"]).Bind("Demo.Level", ["Demo:Level"]))
            .BuildServiceProvider();

        Assert.Equal(["Demo.Level"], BoundNames(provider));
    }

    // 组合根拆分时各模块各声明自己的绑定，后一次调用不能把前一次的绑定吞掉
    [Fact]
    public async Task Bindings_of_different_settings_accumulate()
    {
        var store = new FakeSettingStore();
        store.Host["Demo.Level"] = "Debug";
        store.Host["Demo.Days"] = "120";

        using var host = await StartAsync(
            store,
            bindings => bindings.Bind("Demo.Level", ["Demo:Level"]),
            bindings => bindings.Bind("Demo.Days", ["Demo:Days"]));

        Assert.Equal(["Demo.Level", "Demo.Days"], BoundNames(host.Services));
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        Assert.Equal(("Debug", "120"), (configuration["Demo:Level"], configuration["Demo:Days"]));
    }

    // 同名不同登记必须在登记时失败：推迟到首次解析，错误会落在某个无关消费方的调用栈上
    [Theory]
    [InlineData("target", "keys [Demo:Other]")]
    [InlineData("fallback", "fallback 'Warning'")]
    [InlineData("key-order", "keys [Demo:Level:Default, Demo:Level]")]
    public void A_different_binding_for_the_same_setting_is_rejected_at_registration(string difference, string conflicting)
    {
        Action<HostSettingBindingBuilder> registered = bindings =>
            bindings.Bind("Demo.Level", ["Demo:Level", "Demo:Level:Default"], fallback: "Information");
        Action<HostSettingBindingBuilder> other = difference switch
        {
            "target" => bindings => bindings.Bind("Demo.Level", ["Demo:Other"], fallback: "Information"),
            "fallback" => bindings => bindings.Bind("Demo.Level", ["Demo:Level", "Demo:Level:Default"], fallback: "Warning"),
            "key-order" => bindings => bindings.Bind("Demo.Level", ["Demo:Level:Default", "Demo:Level"], fallback: "Information"),
            _ => throw new ArgumentOutOfRangeException(nameof(difference)),
        };
        var services = Base().AddHostSettings(registered);

        var error = Assert.Throws<InvalidOperationException>(() => services.AddHostSettings(other));

        Assert.Contains("'Demo.Level'", error.Message, StringComparison.Ordinal);
        Assert.Contains("keys [Demo:Level, Demo:Level:Default], fallback 'Information', options type none", error.Message, StringComparison.Ordinal);
        Assert.Contains(conflicting, error.Message, StringComparison.Ordinal);
        // 失败的调用不留下半截绑定：先前的登记原样保留
        using var provider = services.BuildServiceProvider();
        var binding = Assert.Single(provider.GetRequiredService<IOptions<HostSettingBindingCollection>>().Value.Bindings);
        Assert.Equal(["Demo:Level", "Demo:Level:Default"], binding.ConfigurationKeys);
    }

    // 键与兜底值相同、只是一处按选项类型绑定：后者应用时要整组校验，两者不能当成同一登记
    [Fact]
    public void A_binding_that_differs_only_in_options_type_is_rejected_at_registration()
    {
        var services = Base().AddHostSettings(bindings => bindings.Bind("Demo.Days", ["Demo:Days"], fallback: "365"));

        var error = Assert.Throws<InvalidOperationException>(
            () => services.AddHostSettings(bindings => bindings.BindOption<DemoOptions>("Demo.Days", "Demo", nameof(DemoOptions.Days))));

        Assert.Contains("options type none", error.Message, StringComparison.Ordinal);
        Assert.Contains($"options type {typeof(DemoOptions).FullName}", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, HostSettingOptions.SectionName)]
    [InlineData("Ops:HostSettings", "Ops:HostSettings")]
    public void Refresh_interval_validation_names_the_actual_section(string? configSectionPath, string expectedSection)
    {
        var services = Base(new Dictionary<string, string?> { [$"{expectedSection}:RefreshInterval"] = "00:00:00.5" });
        if (configSectionPath is null)
        {
            services.AddHostSettings(Bind);
        }
        else
        {
            services.AddHostSettings(Bind, configSectionPath);
        }

        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<HostSettingOptions>>().Value);
        Assert.StartsWith($"{expectedSection}:RefreshInterval", Assert.Single(failure.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void The_refresh_interval_binds_from_a_custom_section()
    {
        using var provider = Base(new Dictionary<string, string?> { ["Ops:HostSettings:RefreshInterval"] = "00:02:00" })
            .AddHostSettings(Bind, "Ops:HostSettings")
            .BuildServiceProvider();

        Assert.Equal(TimeSpan.FromMinutes(2), provider.GetRequiredService<IOptions<HostSettingOptions>>().Value.RefreshInterval);
    }

    // 刷新周期只有一份：第二次换路径时若静默接受，校验消息会报一个没人配置的键
    [Fact]
    public void Repeated_registration_with_another_section_is_rejected()
    {
        var services = Base().AddHostSettings(Bind);

        Assert.Throws<InvalidOperationException>(() => services.AddHostSettings(Bind, "Ops:HostSettings"));
    }

    // 宿主自己提供的定义不受影响：宿主级设置只追加一个定义提供程序，与宿主的并存
    [Fact]
    public void Host_definition_providers_coexist_with_the_defaults_provider()
    {
        var services = Base();
        services.AddSingleton<ISettingDefinitionProvider, HostDefinitions>();

        services.AddHostSettings(Bind);

        Assert.Equal(2, services.Count(d => d.ServiceType == typeof(ISettingDefinitionProvider)));
        Assert.Contains(services, d => d.ImplementationType == typeof(HostDefinitions));
    }

    private sealed class HostDefinitions : ISettingDefinitionProvider
    {
        public void Define(ISettingDefinitionContext context)
        {
        }
    }

    private sealed class DemoDefinitions : ISettingDefinitionProvider
    {
        public void Define(ISettingDefinitionContext context)
        {
            context.Add("Demo.Level", scopes: SettingScopes.Host);
            context.Add("Demo.Days", scopes: SettingScopes.Host).AsInteger(30, 3650);
        }
    }

    private sealed class DemoOptions
    {
        public int Days { get; set; } = 365;
    }
}
