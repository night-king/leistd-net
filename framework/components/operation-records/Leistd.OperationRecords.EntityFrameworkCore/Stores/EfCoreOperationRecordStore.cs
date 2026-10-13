using Leistd.Data.Filters;
using Leistd.Data.Paging;
using Leistd.Data.Querying;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Tenancy;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Stores;
using Leistd.OperationRecords.EntityFrameworkCore.Entities;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.EntityFrameworkCore;

namespace Leistd.OperationRecords.EntityFrameworkCore.Stores;

/// <summary>使用 EF Core 持久化操作记录，并提供历史读取。</summary>
/// <remarks>
/// <para>通过 <see cref="IDbContextProvider{TDbContext}"/> 参与工作单元：成功记录与业务数据同一事务提交。</para>
/// <para>原表的租户隔离由 <c>IMultiTenant</c> 的全局查询过滤器承担；计数同时读不实现 <c>IMultiTenant</c> 的归档表，
/// 在那里按当前租户显式过滤，<c>IDataFilter.Disable&lt;IMultiTenant&gt;()</c> 时与原表一样放开。</para>
/// </remarks>
/// <typeparam name="TDbContext">宿主 DbContext 类型（需包含 OperationRecord 配置）。</typeparam>
/// <param name="dbContextProvider">工作单元内的 DbContext 提供器。</param>
/// <param name="unitOfWorkManager">工作单元管理器，失败记录据它独立提交。</param>
/// <param name="currentTenant">当前租户上下文，失败记录据它切到记录所在的层，计数据它过滤归档表。</param>
/// <param name="dataFilter">数据过滤开关；未注册时租户过滤恒开启，与 DDD 基座的上下文同口径。</param>
public class EfCoreOperationRecordStore<TDbContext>(
    IDbContextProvider<TDbContext> dbContextProvider,
    IUnitOfWorkManager unitOfWorkManager,
    ICurrentTenant currentTenant,
    IDataFilter? dataFilter = null) : IOperationRecordWriter, IOperationRecordReader
    where TDbContext : DbContext
{
    /// <inheritdoc />
    /// <remarks>
    /// <para>成功记录就地保存：有环境工作单元时落进它的事务，由它决定提交还是回滚；没有时保存即生效。</para>
    /// <para>失败记录先切到记录所在的租户层，再在新开的工作单元里写入并提交；切租户须在开工作单元之前，
    /// 工作单元按开启时的租户绑定连接。</para>
    /// </remarks>
    public async Task InsertAsync(OperationRecordInfo record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Outcome == OperationRecordOutcome.Succeeded)
        {
            // 成功记录跟随调用方的事务，连的是当前租户的库，归属必须一致
            if (record.TenantId != currentTenant.Id)
            {
                throw new InvalidOperationException(
                    $"A succeeded operation record for tenant '{record.TenantId}' cannot be written "
                    + $"inside the context of tenant '{currentTenant.Id}'.");
            }

            await AddAsync(record, cancellationToken);
            return;
        }

        // 同层时不切：切换会丢掉当前上下文里的租户名
        using (record.TenantId == currentTenant.Id ? null : currentTenant.Change(record.TenantId))
        using (var unitOfWork = unitOfWorkManager.Begin(requiresNew: true))
        {
            await AddAsync(record, cancellationToken);
            await unitOfWork.CompleteAsync(cancellationToken);
        }
    }

    private async Task AddAsync(OperationRecordInfo record, CancellationToken cancellationToken)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        dbContext.Set<OperationRecord>().Add(OperationRecord.FromInfo(record));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PagedResult<OperationRecordInfo>> GetPagedListAsync(
        OperationRecordCriteria criteria,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(criteria.Scope);
        ArgumentNullException.ThrowIfNull(page);

        var scope = criteria.Scope;

        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var query = dbContext.Set<OperationRecord>().AsNoTracking();

        // 先物化成数组，EF 才翻译为 IN (...)；IReadOnlyCollection 在部分 Provider 上会退化为客户端求值
        if (criteria.Actions is { Count: > 0 } actions)
        {
            var actionCodes = actions.ToArray();
            query = query.Where(x => actionCodes.Contains(x.Action));
        }

        if (criteria.Outcome is { } requiredOutcome)
        {
            query = query.Where(x => x.Outcome == requiredOutcome);
        }

        // 可见性过滤在数据库里做，总数与分页才正确；租户维度由 IMultiTenant 的全局查询过滤器承担
        if (scope.IsRestricted)
        {
            var actorId = scope.ActorId;
            var actorTenantId = scope.ActorTenantId;
            var includesHostRecords = scope.IncludesHostRecords;
            // 先落成局部变量再进表达式树，避免把 scope 对象捕获进查询。
            // Actor 层对宿主（即能看 Host 层的读者）整层放行，否则宿主的 ActorId 为 null 会滤掉整层；
            // 对其余读者只放行标识与所属租户都相同的“本人”。
            query = query.Where(x =>
                x.Visibility == OperationVisibility.Tenant
                || (x.Visibility == OperationVisibility.Host && includesHostRecords)
                || (x.Visibility == OperationVisibility.Actor
                    && (includesHostRecords || (actorId != null && x.ActorId == actorId && x.ActorTenantId == actorTenantId))));
        }

        if (!string.IsNullOrWhiteSpace(criteria.Keyword))
        {
            var trimmed = criteria.Keyword.Trim();
            query = query.Where(x =>
                x.Action.Contains(trimmed)
                || x.TargetId.Contains(trimmed)
                || (x.TargetName != null && x.TargetName.Contains(trimmed))
                || (x.ActorName != null && x.ActorName.Contains(trimmed)));
        }

        // 两端闭区间：按天选择时调用方把上界取到当天 23:59:59.999
        if (criteria.StartTime is { } startTime)
        {
            query = query.Where(x => x.CreationTime >= startTime);
        }

        if (criteria.EndTime is { } endTime)
        {
            query = query.Where(x => x.CreationTime <= endTime);
        }

        var totalCount = await query.LongCountAsync(cancellationToken);

        // 次级键让同一时刻的记录顺序确定，分页不重复不遗漏；不保证同一时刻内的先后（取决于 Provider 的 Guid 比较）
        var items = await query
            .OrderByDescending(x => x.CreationTime)
            .ThenByDescending(x => x.Id)
            .Skip(page.Offset)
            .Take(page.Limit)
            .ToListAsync(cancellationToken);

        return new PagedResult<OperationRecordInfo>(totalCount, [.. items.Select(x => x.ToInfo())]);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> CountByBucketsAsync(
        OperationRecordVisibilityScope scope,
        IReadOnlyList<DateTime> edges,
        IReadOnlyCollection<string>? actions = null,
        bool distinctTargets = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(edges);
        if (edges.Count < 2)
        {
            throw new ArgumentException("At least two bucket edges are required.", nameof(edges));
        }

        // 区间筛选与分组由 GroupByTimeBucket 按首尾边界完成，这里只放可见范围与动作
        var records = await CountedRecordsAsync(scope, actions, cancellationToken);
        if (distinctTargets)
        {
            records = records.Where(x => x.TargetId != OperationTarget.NoTargetId);
        }

        // 分组与计数在数据库里完成，每个有记录的桶只取回一行
        var buckets = records.GroupByTimeBucket(x => x.CreationTime, edges);
        var rows = distinctTargets
            ? await buckets
                .Select(group => new { Bucket = group.Key, Count = group.Select(x => x.TargetId).Distinct().Count() })
                .ToListAsync(cancellationToken)
            : await buckets
                .Select(group => new { Bucket = group.Key, Count = group.Count() })
                .ToListAsync(cancellationToken);

        var counts = new int[edges.Count - 1];
        foreach (var row in rows)
        {
            counts[row.Bucket] = row.Count;
        }

        return counts;
    }

    // 原表与归档表投影成同一形状后 UNION ALL，筛选与聚合在一条语句里完成：
    // 归档作业同时在搬运时，两次分开的查询会把同一条记录数两遍或漏掉
    private async Task<IQueryable<CountedRecord>> CountedRecordsAsync(
        OperationRecordVisibilityScope scope,
        IReadOnlyCollection<string>? actions,
        CancellationToken cancellationToken)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);

        var archived = dbContext.Set<OperationRecordArchive>().AsNoTracking();
        if (dataFilter?.IsEnabled<IMultiTenant>() ?? true)
        {
            // 宿主层单独写成 IS NULL，两种写法都能走 (TenantId, CreationTime) 索引
            var tenantId = currentTenant.Id;
            archived = tenantId is null
                ? archived.Where(x => x.TenantId == null)
                : archived.Where(x => x.TenantId == tenantId);
        }

        var query = dbContext.Set<OperationRecord>().AsNoTracking()
            .Select(x => new CountedRecord
            {
                CreationTime = x.CreationTime,
                Action = x.Action,
                TargetId = x.TargetId,
                Visibility = x.Visibility,
                ActorId = x.ActorId,
                ActorTenantId = x.ActorTenantId
            })
            .Concat(archived.Select(x => new CountedRecord
            {
                CreationTime = x.CreationTime,
                Action = x.Action,
                TargetId = x.TargetId,
                Visibility = x.Visibility,
                ActorId = x.ActorId,
                ActorTenantId = x.ActorTenantId
            }));

        if (actions is { Count: > 0 })
        {
            var actionCodes = actions.ToArray();
            query = query.Where(x => actionCodes.Contains(x.Action));
        }

        // 与分页同一套可见性判定，见 GetPagedListAsync
        if (scope.IsRestricted)
        {
            var actorId = scope.ActorId;
            var actorTenantId = scope.ActorTenantId;
            var includesHostRecords = scope.IncludesHostRecords;
            query = query.Where(x =>
                x.Visibility == OperationVisibility.Tenant
                || (x.Visibility == OperationVisibility.Host && includesHostRecords)
                || (x.Visibility == OperationVisibility.Actor
                    && (includesHostRecords || (actorId != null && x.ActorId == actorId && x.ActorTenantId == actorTenantId))));
        }

        return query;
    }

    // 计数用到的列；两张表投影成它才能合并
    private sealed class CountedRecord
    {
        public DateTime CreationTime { get; init; }

        public string Action { get; init; } = default!;

        public string TargetId { get; init; } = default!;

        public OperationVisibility Visibility { get; init; }

        public string? ActorId { get; init; }

        public Guid? ActorTenantId { get; init; }
    }
}
