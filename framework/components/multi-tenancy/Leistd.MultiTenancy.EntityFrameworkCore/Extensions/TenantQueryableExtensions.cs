using Microsoft.EntityFrameworkCore;
using Leistd.MultiTenancy.EntityFrameworkCore.Entities;

namespace Leistd.MultiTenancy.EntityFrameworkCore.Extensions;

/// <summary>提供排除已删除租户的控制面查询入口。</summary>
/// <remarks>
/// 控制面上下文通常是普通 <see cref="DbContext"/>，没有软删除过滤器；软删租户的连接行仍在库里（级联只在硬删时触发）。
/// 查这两张表必须从本类的入口起查。
/// </remarks>
public static class TenantQueryableExtensions
{
    /// <summary>查询未删除的租户记录。</summary>
    public static IQueryable<TenantRecord> UndeletedTenants(this DbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        return dbContext.Set<TenantRecord>().Where(t => !t.IsDeleted);
    }

    /// <summary>查询未删除租户的连接配置记录。</summary>
    public static IQueryable<TenantConnectionRecord> ConnectionsOfUndeletedTenants(this DbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        return dbContext.Set<TenantConnectionRecord>()
            .Join(
                dbContext.UndeletedTenants(),
                connection => connection.TenantId,
                tenant => tenant.Id,
                (connection, _) => connection);
    }
}
