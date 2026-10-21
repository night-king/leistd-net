using Leistd.BackgroundJobs;
using Leistd.BackgroundJobs.Recurring;
using Leistd.EventBus.EventHandlers;
using Leistd.Settings.Definitions;
using Leistd.Settings.Events;
using Leistd.Settings.Hosting.Configuration;
using Leistd.Settings.Hosting.Definitions;
using Leistd.Settings.Hosting.Options;
using Leistd.Settings.Hosting.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Leistd.Settings.Hosting;

/// <summary>宿主级设置 → 配置源 → <c>IOptionsMonitor</c> 的注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>让宿主级设置覆盖部署配置：消费方照常注入 <c>IOptionsMonitor&lt;T&gt;</c>，不必知道值来自设置表。</summary>
    /// <remarks>
    /// <para>值在三处推进配置：宿主开始接收请求之前一次；写入宿主级设置的事务提交后本进程立即一次；
    /// 其余时候由每个副本上的 <c>EveryInstance</c> 周期任务按 <see cref="HostSettingOptions.RefreshInterval"/> 跟上
    /// （需要宿主注册调度器，如 <c>AddInProcessBackgroundJobs()</c>）。</para>
    /// <para>构建之后还要调用 <see cref="UseHostSettings{THost}"/> 把配置源挂上，漏了启动时抛出。
    /// 可重复调用：不同设置的绑定累加，完全相同的绑定不重复生效；同一设置名的绑定不同时在本次调用就抛出
    /// <see cref="InvalidOperationException"/>（判定见 <see cref="HostSettingBindingBuilder"/>），<paramref name="bind"/> 在调用时即执行。
    /// 刷新周期绑定 <paramref name="configSectionPath"/>，重复调用换用另一配置节时抛出 <see cref="InvalidOperationException"/>。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddHostSettings(bindings => bindings
    ///     .Bind("Logging.MinimumLevel", ["Serilog:MinimumLevel", "Serilog:MinimumLevel:Default"], fallback: "Information")
    ///     .BindOption&lt;SmtpOptions&gt;("Email.SmtpHost", SmtpOptions.SectionName, nameof(SmtpOptions.Host)));
    ///
    /// var app = builder.Build();
    /// app.UseHostSettings();
    /// </code>
    /// </example>
    /// <param name="services">服务集合。</param>
    /// <param name="bind">声明绑定。</param>
    /// <param name="configSectionPath"><see cref="HostSettingOptions"/> 绑定的配置节，校验消息按它报键名。</param>
    public static IServiceCollection AddHostSettings(
        this IServiceCollection services,
        Action<HostSettingBindingBuilder> bind,
        string configSectionPath = HostSettingOptions.SectionName)
    {
        ArgumentNullException.ThrowIfNull(bind);
        ArgumentException.ThrowIfNullOrWhiteSpace(configSectionPath);

        // 刷新周期只有一份：换用另一配置节的重复调用会让校验消息报错键名
        if (services.Select(descriptor => descriptor.ImplementationInstance).OfType<HostSettingOptionsValidator>().FirstOrDefault()
                is { } registered && registered.ConfigSectionPath != configSectionPath)
        {
            throw new InvalidOperationException(
                $"AddHostSettings() already binds '{registered.ConfigSectionPath}'; it cannot also bind '{configSectionPath}'.");
        }

        // 在登记时执行绑定声明：冲突此时就抛出，且失败时已登记的绑定保持不变
        var collection = services.Select(descriptor => descriptor.ImplementationInstance)
            .OfType<IOptions<HostSettingBindingCollection>>().FirstOrDefault()?.Value;
        List<HostSettingBinding> pending = [.. collection?.Bindings ?? []];
        bind(new HostSettingBindingBuilder(pending));
        if (collection is null)
        {
            collection = new HostSettingBindingCollection();
            services.AddSingleton<IOptions<HostSettingBindingCollection>>(new OptionsWrapper<HostSettingBindingCollection>(collection));
        }

        collection.Bindings.Clear();
        collection.Bindings.AddRange(pending);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(HostSettingsConfigurationProvider)))
        {
            return services;
        }

        services.AddSingleton(new HostSettingsConfigurationProvider());
        services.AddSingleton<ISettingDefinitionProvider, HostSettingDefaultsProvider>();
        services.TryAddScoped<HostSettingApplier>();
        services.AddScoped<IEventHandler<SettingChangedEvent>, HostSettingChangedHandler>();
        services.AddHostedService<HostSettingStartupService>();

        services.AddOptions<HostSettingOptions>()
            .BindConfiguration(configSectionPath)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<HostSettingOptions>>(new HostSettingOptionsValidator(configSectionPath));
        services.AddRecurringJob<HostSettingRefreshJob>(
            HostSettingRefreshJob.Name,
            provider => RecurringJobSchedule.Every(provider.GetRequiredService<IOptions<HostSettingOptions>>().Value.RefreshInterval),
            RecurringJobScope.EveryInstance);

        return services;
    }

    /// <summary>把宿主级设置作为优先级最高的配置源挂到宿主配置上。</summary>
    /// <remarks>
    /// 须在构建之后调用：构建期间追加的配置源（如测试宿主的覆盖配置）否则会排在后面、压过设置。
    /// </remarks>
    /// <typeparam name="THost">宿主类型。</typeparam>
    /// <param name="host">已构建的宿主。</param>
    public static THost UseHostSettings<THost>(this THost host)
        where THost : IHost
    {
        ArgumentNullException.ThrowIfNull(host);

        var provider = host.Services.GetService<HostSettingsConfigurationProvider>()
            ?? throw new InvalidOperationException("Call AddHostSettings() before UseHostSettings().");
        if (provider.IsAttached)
        {
            return host;
        }

        if (host.Services.GetRequiredService<IConfiguration>() is not IConfigurationBuilder configuration)
        {
            throw new InvalidOperationException(
                "The host configuration does not accept new sources; host settings need a ConfigurationManager-based host.");
        }

        configuration.Add(new HostSettingsConfigurationSource(provider));
        return host;
    }
}
