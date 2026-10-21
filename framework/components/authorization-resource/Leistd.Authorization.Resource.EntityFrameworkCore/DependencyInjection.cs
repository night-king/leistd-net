using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Leistd.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Leistd.Authorization.Resource.EntityFrameworkCore.EntityConfigurations;
using Leistd.Authorization.Resource.EntityFrameworkCore.Managers;
using Leistd.Authorization.Resource.EntityFrameworkCore.Stores;
using Leistd.Authorization.Resource.Grants;

namespace Leistd.Authorization.Resource.EntityFrameworkCore;

/// <summary>资源 ACL 的 EF Core 持久化注册与模型配置。</summary>
public static class DependencyInjection
{
    /// <summary>注册基于指定 DbContext 的资源 ACL 存储与管理器。</summary>
    /// <remarks>
    /// <para>只注册 ACL 存储；判定入口在 <c>Leistd.Authorization.Resource.AspNetCore</c> 的 <c>AddResourceAuthorization()</c>。</para>
    /// <para>宿主须已注册 <c>AddUnitOfWork()</c> 与 <c>AddUnitOfWorkEfCore()</c>：存储与管理器经
    /// <c>IDbContextProvider&lt;TDbContext&gt;</c> 取得本工作单元已解析连接的上下文。</para>
    /// <para>同一 DbContext 重复调用幂等；已用另一 DbContext 或其他实现注册过 ACL 存储时抛出 <see cref="InvalidOperationException"/>。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddResourceAuthorizationEfCore&lt;AppDbContext&gt;();
    /// builder.Services.AddResourceAuthorization();   // Leistd.Authorization.Resource.AspNetCore
    /// </code>
    /// </example>
    public static IServiceCollection AddResourceAuthorizationEfCore<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        // ACL 存储只有一个权威实现：两个上下文各注册一次时会静默取其一。管理器允许宿主替换。
        services.EnsureSingleAuthoritative<IResourceGrantStore, EfCoreResourceGrantStore<TDbContext>>(
            ServiceLifetime.Transient,
            "Resource grants have a single authoritative store; map the resource ACL tables in one DbContext.");

        services.AddResourceAuthorizationCore();
        // 无状态：上下文经 IDbContextProvider 按工作单元取，与本服务的实例生命周期无关
        services.TryAddTransient<IResourceGrantStore, EfCoreResourceGrantStore<TDbContext>>();
        services.TryAddTransient<IResourceGrantManager, EfCoreResourceGrantManager<TDbContext>>();
        return services;
    }

    /// <summary>映射资源 ACL 实体，在 OnModelCreating 中调用。</summary>
    public static ModelBuilder ConfigureResourceAuthorization(this ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new ResourcePermissionGrantRecordConfiguration());
        modelBuilder.ApplyConfiguration(new ResourceAuthorizationVersionRecordConfiguration());
        return modelBuilder;
    }
}
