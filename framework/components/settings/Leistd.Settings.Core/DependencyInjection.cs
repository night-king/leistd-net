using Leistd.ExceptionHandling.Options;
using Leistd.Settings.ExceptionMappings;
using Leistd.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Leistd.Settings.Definitions;
using Leistd.Settings.Errors;
using Leistd.Settings.Management;
using Leistd.Settings.Resolution;
using Leistd.Settings.Stores;
using Leistd.Settings.Options;

namespace Leistd.Settings;

/// <summary>设置定义与解析的核心注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册设置定义管理器、解析器、写入入口与设置页用例。</summary>
    /// <remarks>
    /// <para><see cref="ISettingDefinitionManager"/> 为 Singleton——定义在进程生命周期内不变；
    /// <see cref="ISettingProvider"/> 为 Scoped，按当前租户与当前用户缓存，同一作用域里每个租户、每个用户只查一次库。</para>
    /// <para>还需要一个 <see cref="ISettingStore"/> 实现（如 <c>AddSettingsEfCore&lt;TDbContext&gt;()</c>），
    /// 缺失时解析 <see cref="ISettingProvider"/> 失败。</para>
    /// <para>机密设置（<see cref="ISettingDefinition.IsEncrypted"/>）用宿主的 Data Protection 加解密：
    /// 宿主须 <c>AddDataProtection()</c> 并配置持久化、可共享的密钥环；没注册时只有读写机密设置会失败，
    /// 不定义机密设置的宿主不受影响。</para>
    /// <para>可重复调用：服务只注册一次，<paramref name="configure"/> 每次都叠加。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddSettingsCore(options => options.LocalizationResource = typeof(AppResource));
    /// builder.Services.AddSingleton&lt;ISettingDefinitionProvider, DisplaySettingDefinitionProvider&gt;();
    /// </code>
    /// </example>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">设置页用例的展示约定。</param>
    public static IServiceCollection AddSettingsCore(
        this IServiceCollection services,
        Action<SettingManagementOptions>? configure = null)
    {
        services.TryAddSingleton<ISettingDefinitionManager, SettingDefinitionManager>();
        services.TryAddScoped<ISettingProvider, DefaultSettingProvider>();
        services.TryAddTransient<ISettingManager, DefaultSettingManager>();
        services.TryAddTransient<ISettingManagementService, SettingManagementService>();
        services.AddOptions<SettingManagementOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddJsonLocalizationResources(typeof(SettingErrorCodes).Assembly);
        // 错误码的状态语义与默认译文属于本组件默认值，在此登记，避免宿主漏配时静默回落成 400。
        // 宿主的 MapCode / MapException 覆盖同一码或同一类型，与调用顺序无关。
        services.Configure<GlobalExceptionOptions>(SettingsExceptionMappings.Configure);
        return services;
    }
}
