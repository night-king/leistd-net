using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Leistd.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Leistd.Authorization.EntityFrameworkCore.EntityConfigurations;
using Leistd.Authorization.EntityFrameworkCore.Managers;
using Leistd.Authorization.EntityFrameworkCore.Stores;
using Leistd.Authorization.EntityFrameworkCore.Entities;
using Leistd.Authorization.Grants;

namespace Leistd.Authorization.EntityFrameworkCore;

/// <summary>权限 EF Core 持久化的注册与模型配置。</summary>
public static class DependencyInjection
{
    /// <summary>注册 EF Core 权限授予存储（基于指定 DbContext）。</summary>
    /// <remarks>
    /// 宿主须已注册 <c>AddUnitOfWork()</c> 与 <c>AddUnitOfWorkEfCore()</c>：存储与管理器经
    /// <c>IDbContextProvider&lt;TDbContext&gt;</c> 取得本工作单元已解析连接的上下文。
    /// 同一 DbContext 重复调用幂等；已用另一 DbContext 或其他实现注册过授予存储时抛出 <see cref="InvalidOperationException"/>。
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddPermissionAuthorizationEfCore&lt;AppDbContext&gt;();
    ///
    /// // 写入统一经管理器：自动补齐祖先、级联清理子孙
    /// await grantManager.GrantAsync("Orders.Update", PermissionGrantProviderNames.Role, "admin", ct);
    /// </code>
    /// </example>
    public static IServiceCollection AddPermissionAuthorizationEfCore<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        // 授予存储只有一个权威实现：两个上下文各注册一次时会静默取其一。管理器允许宿主替换。
        services.EnsureSingleAuthoritative<IPermissionGrantStore, EfCorePermissionGrantStore<TDbContext>>(
            ServiceLifetime.Transient,
            "Permission grants have a single authoritative store; map the authorization tables in one DbContext.");

        services.AddPermissionAuthorizationCore();
        services.TryAddTransient<IPermissionGrantStore, EfCorePermissionGrantStore<TDbContext>>();
        services.TryAddTransient<IPermissionGrantManager, EfCorePermissionGrantManager<TDbContext>>();
        return services;
    }

    /// <summary>映射 <see cref="PermissionGrantRecord"/> 与 <see cref="AuthorizationVersionRecord"/>，在 OnModelCreating 中调用。</summary>
    public static ModelBuilder ConfigurePermissionAuthorization(this ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new PermissionGrantRecordConfiguration());
        modelBuilder.ApplyConfiguration(new AuthorizationVersionRecordConfiguration());
        return modelBuilder;
    }
}
