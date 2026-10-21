using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Leistd.UnitOfWork.EntityFrameworkCore;

/// <summary>工作单元的 EF Core 集成注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册工作单元的 EF Core 支持。</summary>
    /// <remarks>可重复调用：服务只注册一次。</remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddUnitOfWork();
    /// builder.Services.AddUnitOfWorkEfCore();
    ///
    /// // 仓储与管理器统一经 IDbContextProvider 取上下文，自动绑定当前工作单元的连接
    /// var db = await dbContextProvider.GetDbContextAsync(ct);
    /// </code>
    /// </example>
    public static IServiceCollection AddUnitOfWorkEfCore(
        this IServiceCollection services)
    {
        services.TryAddTransient(typeof(IDbContextProvider<>), typeof(DbContextProvider<>));
        services.TryAddScoped<UnitOfWorkConnectionBinding>();
        // 物理目标由宿主配置，按 DbContext 类型探测一次即可。
        services.TryAddSingleton<DbContextConfiguredTargetCache>();

        return services;
    }
}
