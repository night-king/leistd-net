using Leistd.ExceptionHandling.Options;
using Leistd.Authorization.ExceptionMappings;
using Leistd.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Leistd.Authorization.Checking;
using Leistd.Authorization.Definitions;
using Leistd.Authorization.Grants;
using Leistd.Authorization.Management;
using Leistd.Authorization.Errors;
using Leistd.Authorization.Subjects;
using Leistd.Authorization.Options;

namespace Leistd.Authorization;

/// <summary>权限定义与检查的核心注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册权限定义管理器、默认权限检查器、管理用例与首次授予。</summary>
    /// <remarks>
    /// <para><see cref="IPermissionDefinitionManager"/> 为 Singleton，<see cref="IPermissionChecker"/> 为 Scoped——
    /// 一次请求内的多次权限检查共享同一份主体与授予快照。</para>
    /// <para>管理用例与首次授予还需要授予存储与管理器（如 <c>AddPermissionAuthorizationEfCore&lt;TDbContext&gt;()</c>）；
    /// 管理用例另需宿主实现 <see cref="IPermissionSubjectDirectory"/>，缺失时解析它直接失败。
    /// 可重复调用：服务只注册一次，<paramref name="configure"/> 每次都叠加。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddPermissionAuthorizationCore(options => options.LocalizationResource = typeof(AppResource));
    /// builder.Services.AddSingleton&lt;IPermissionDefinitionProvider, OrderPermissionDefinitionProvider&gt;();
    /// builder.Services.AddScoped&lt;IPermissionSubjectDirectory, RoleSubjectDirectory&gt;();
    /// </code>
    /// </example>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">管理用例的展示约定。</param>
    public static IServiceCollection AddPermissionAuthorizationCore(
        this IServiceCollection services,
        Action<PermissionManagementOptions>? configure = null)
    {
        services.TryAddSingleton<IPermissionDefinitionManager, PermissionDefinitionManager>();
        services.TryAddScoped<IPermissionChecker, DefaultPermissionChecker>();
        services.TryAddTransient<IPermissionManagementService, PermissionManagementService>();
        services.TryAddTransient<IPermissionGrantSeeder, PermissionGrantSeeder>();
        services.AddOptions<PermissionManagementOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddJsonLocalizationResources(typeof(PermissionErrorCodes).Assembly);
        // 错误码的状态语义与默认译文属于本组件默认值，在此登记，避免宿主漏配时静默回落成 400。
        // 宿主的 MapCode / MapException 覆盖同一码或同一类型，与调用顺序无关。
        services.Configure<GlobalExceptionOptions>(AuthorizationExceptionMappings.Configure);
        return services;
    }
}
