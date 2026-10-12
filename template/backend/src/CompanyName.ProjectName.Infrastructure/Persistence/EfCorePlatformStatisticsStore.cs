using CompanyName.ProjectName.Domain.Statistics.Abstractions;
#if (IncludeOperationRecords)
using Leistd.OperationRecords.EntityFrameworkCore.Entities;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
#endif
#if (IncludeMultiTenancy)
using Leistd.MultiTenancy.EntityFrameworkCore.Extensions;
#endif
using Microsoft.EntityFrameworkCore;

namespace CompanyName.ProjectName.Infrastructure.Persistence;

/// <summary><see cref="IPlatformStatisticsStore"/> 的 EF Core 实现。</summary>
/// <remarks>
#if (IncludeOperationRecords)
/// <para>操作记录经 <see cref="IDbContextProvider{TDbContext}"/> 取业务上下文：连接按当前租户解析，
/// 全局租户过滤器只留当前层的记录（宿主只看 <c>TenantId</c> 为空的行），不跨租户、不遍历租户库。</para>
#endif
#if (IncludeMultiTenancy)
/// <para>租户注册表在固定连宿主库的控制面上下文里，从排除已删除租户的入口起查。</para>
#endif
/// </remarks>
public class EfCorePlatformStatisticsStore(
#if (IncludeOperationRecords && IncludeMultiTenancy)
    IDbContextProvider<MyProjectDbContext> dbContextProvider,
    IdentityControlDbContext controlDbContext) : IPlatformStatisticsStore
#elif (IncludeOperationRecords)
    IDbContextProvider<MyProjectDbContext> dbContextProvider) : IPlatformStatisticsStore
#else
    IdentityControlDbContext controlDbContext) : IPlatformStatisticsStore
#endif
{
#if (IncludeOperationRecords)
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<DateOnly, int>> CountOperationRecordsByDayAsync(
        string action,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        // 按 UTC 日分组在数据库里完成（timestamptz 截到 UTC 零点），只取回每天一行
        var days = await dbContext.Set<OperationRecord>()
            .AsNoTracking()
            .Where(record => record.Action == action && record.CreationTime >= start && record.CreationTime < end)
            .GroupBy(record => record.CreationTime.Date)
            .Select(group => new { Day = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        return days.ToDictionary(day => DateOnly.FromDateTime(day.Day), day => day.Count);
    }

    /// <inheritdoc />
    public async Task<int> CountOperationRecordTargetsAsync(
        string action,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        return await dbContext.Set<OperationRecord>()
            .AsNoTracking()
            .Where(record => record.Action == action && record.CreationTime >= start && record.CreationTime < end)
            .Select(record => record.TargetId)
            .Distinct()
            .CountAsync(cancellationToken);
    }
#endif
#if (IncludeMultiTenancy)

    /// <inheritdoc />
    public Task<int> CountInactiveTenantsAsync(CancellationToken cancellationToken = default) =>
        controlDbContext.UndeletedTenants().CountAsync(tenant => !tenant.IsActive, cancellationToken);
#endif
}
