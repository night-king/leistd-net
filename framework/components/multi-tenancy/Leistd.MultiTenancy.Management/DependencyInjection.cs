using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Management.Provisioning;
using Leistd.MultiTenancy.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Leistd.MultiTenancy.Management;

/// <summary>租户管理用例的注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册租户管理与连接管理两个用例。</summary>
    /// <remarks>
    /// <para>用例只依赖契约（<see cref="ITenantManager"/>、<see cref="ITenantStore"/>、
    /// <see cref="ITenantConnectionConfigurationManager"/>、<see cref="ITenantConnectionDirectory"/>、
    /// <c>ITenantDatabaseDirectory</c>）与工作单元，
    /// 六个存储契约与 <c>AddUnitOfWork()</c> 须由调用方先注册，缺失时在首次解析用例时才暴露；
    /// EF 存储的 <c>AddMultiTenancyEfCore</c> 注册了其中的存储。</para>
    /// <para>同时注册开通失败的数据库错误翻译默认实现（不翻译）；宿主注册自己的 <see cref="ITenantDatabaseErrorDescriber"/> 即可替换。</para>
    /// <para>开通编排的顺序与补偿见 <see cref="ITenantProvisioner"/>。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // 自定义存储的宿主：六个存储契约与工作单元都要先就位，再注册用例
    /// builder.Services.AddUnitOfWork();
    /// builder.Services.AddMultiTenancyCore();
    /// builder.Services.AddScoped&lt;ITenantStore, DapperTenantStore&gt;();
    /// builder.Services.AddScoped&lt;ITenantManager, DapperTenantManager&gt;();
    /// builder.Services.AddScoped&lt;ITenantConnectionConfigurationStore, DapperTenantConnectionStore&gt;();
    /// builder.Services.AddScoped&lt;ITenantConnectionConfigurationManager, DapperTenantConnectionManager&gt;();
    /// builder.Services.AddScoped&lt;ITenantConnectionDirectory, DapperTenantConnectionDirectory&gt;();
    /// builder.Services.AddScoped&lt;ITenantDatabaseDirectory, DapperTenantDatabaseDirectory&gt;();
    /// builder.Services.AddTenantManagement();
    /// </code>
    /// </example>
    public static IServiceCollection AddTenantManagement(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMultiTenancyCore();
        // 开通失败的数据库错误翻译：默认不翻译（错误码表随数据库而异），宿主注册自己的实现即可替换
        services.TryAddSingleton<ITenantDatabaseErrorDescriber, NullTenantDatabaseErrorDescriber>();
        services.TryAddTransient<ITenantConnectionManagementService, TenantConnectionManagementService>();
        services.TryAddTransient<ITenantManagementService, TenantManagementService>();
        return services;
    }
}
