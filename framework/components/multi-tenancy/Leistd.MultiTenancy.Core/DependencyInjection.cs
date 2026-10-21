using Leistd.ExceptionHandling.Options;
using Leistd.MultiTenancy.ExceptionMappings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Leistd.MultiTenancy.Stores;
using Leistd.MultiTenancy.Resolution;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Context;
using Leistd.Data.Connections;
using Leistd.MultiTenancy.Errors;
using Leistd.Localization;

namespace Leistd.MultiTenancy;

/// <summary>平台无关的多租户服务注册。</summary>
public static class DependencyInjection
{
    /// <summary>注册当前租户上下文和租户解析器。</summary>
    /// <remarks>
    /// 不注册 <see cref="ITenantStore"/>；宿主必须选择 EF Core 或内存实现。
    /// ASP.NET Core 宿主应使用 Web 集成包的 <c>AddMultiTenancy()</c>。可重复调用，结果与调用一次相同。
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddMultiTenancyCore();
    ///
    /// // 持有注册表的宿主再注册 EF 实现；资源服务不需要 Store（见 ITenantStore）。
    /// builder.Services.AddMultiTenancyEfCore&lt;ControlDbContext&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddMultiTenancyCore(this IServiceCollection services)
    {
        // TryAdd 是替换口：宿主先注册自己的 ICurrentTenantAccessor 即可换掉上下文存储机制，
        // 而 Change() 的嵌套与还原（漏还原即跨租户泄漏）仍由框架的 CurrentTenant 保证。
        services.TryAddSingleton<ICurrentTenantAccessor>(AsyncLocalCurrentTenantAccessor.Instance);
        services.TryAddTransient<ICurrentTenant, CurrentTenant>();
        services.TryAddTransient<ITenantNormalizer, UpperInvariantTenantNormalizer>();
        // 连接相同的共享库租户仍必须绑定不同的工作单元归属。
        services.TryAddTransient<IConnectionAffinityProvider, TenantConnectionAffinityProvider>();
        services.TryAddScoped<ITenantResolver, TenantResolver>();
        // 逐库作业的清单：注册了租户连接解析就列出独立库，没有就只有宿主库，宿主不必按模式分支注册
        services.TryAddTransient<ITenantDatabaseEnumerator, TenantDatabaseEnumerator>();
        services.TryAddTransient<ITenantDatabaseRunner, TenantDatabaseRunner>();
        services.AddJsonLocalizationResources(typeof(MultiTenancyErrorCodes).Assembly);
        // 错误码的状态语义与默认译文属于本组件默认值，在此登记，避免宿主漏配时静默回落成 400。
        // 宿主的 MapCode / MapException 覆盖同一码或同一类型，与调用顺序无关。
        services.Configure<GlobalExceptionOptions>(MultiTenancyExceptionMappings.Configure);
        return services;
    }

    /// <summary>注册远端连接解析：向持有控制库的服务回源租户连接配置，按 <c>Leistd:MultiTenancy:Routing:CacheLifetime</c> 缓存。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configSectionPath"><see cref="TenantRouteCacheOptions"/> 绑定的配置节，校验消息按它报键名。</param>
    /// <remarks>
    /// <para><c>CacheLifetime</c> 默认 10 分钟，可配置（大于 0、不超过 1 小时，越界启动失败）；
    /// 也可以再用 <c>services.Configure&lt;TenantRouteCacheOptions&gt;</c> 覆盖。重复调用换用另一配置节时抛出
    /// <see cref="InvalidOperationException"/>。</para>
    /// <para>宿主须注册 <see cref="ITenantConnectionConfigurationStore"/> 的远端实现（<c>Leistd.MultiTenancy.ServiceClient</c> 包）：控制面经已认证的内部接口下发
    /// 已解密的连接串，本服务不需要控制面的密钥环。
    /// 同时注册多租户核心服务（<see cref="AddMultiTenancyCore"/>）、<see cref="ITenantMigrationTargetProvider"/> 与 <c>HybridCache</c>（只用进程内一级，连接串不进分布式缓存）。均以 <c>TryAdd</c> 注册，宿主可替换。</para>
    /// <para>宿主自己持有控制库时改用 EF 包的 <c>AddLocalTenantConnectionResolution</c>，两者二选一。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddRemoteTenantConnectionResolution();
    /// builder.Services.AddRemoteTenantConnectionStore("identity"); // Leistd.MultiTenancy.ServiceClient
    /// </code>
    /// </example>
    public static IServiceCollection AddRemoteTenantConnectionResolution(
        this IServiceCollection services,
        string configSectionPath = TenantRouteCacheOptions.SectionName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(configSectionPath);

        // 路由缓存只有一份：换用另一配置节的重复调用会让校验消息报错键名
        if (services.Select(descriptor => descriptor.ImplementationInstance).OfType<TenantRouteCacheOptionsValidator>().FirstOrDefault()
                is { } registered && registered.ConfigSectionPath != configSectionPath)
        {
            throw new InvalidOperationException(
                $"AddRemoteTenantConnectionResolution() already binds '{registered.ConfigSectionPath}'; it cannot also bind '{configSectionPath}'.");
        }

        // 解析器按当前租户取连接；只做迁移等不经 Web 集成的宿主也要能单独使用本入口
        services.AddMultiTenancyCore();
        // 回源结果的进程内缓存与并发合并；官方实现以 TryAdd 注册，宿主自己的 AddHybridCache 配置照常生效
        services.AddHybridCache();
        services.AddOptions<TenantRouteCacheOptions>()
            .BindConfiguration(configSectionPath)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TenantRouteCacheOptions>>(
            new TenantRouteCacheOptionsValidator(configSectionPath)));

        // 逐库枚举据此判"有独立库可列"，不从解析器或目录的在场与否推断
        services.TryAddSingleton(TenantConnectionRouting.Instance);
        services.TryAddScoped<IConnectionStringResolver, RemoteConnectionStringResolver>();
        services.TryAddTransient<ITenantMigrationTargetProvider, TenantMigrationTargetProvider>();

        return services;
    }

}
