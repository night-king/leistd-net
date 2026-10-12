namespace CompanyName.ProjectName.Domain.Statistics.Abstractions;

/// <summary>平台统计的计数端口：数据在框架组件的表里，内层引用不到这些实体，由持久化层按条件聚合。</summary>
/// <remarks>
/// 只给数，不给语义：统计哪个动作、区间怎么划、上一区间怎么取由应用层决定。
/// 时间区间一律是 UTC 的半开区间 [start, end)。
/// </remarks>
public interface IPlatformStatisticsStore
{
#if (IncludeOperationRecords)
    /// <summary>按 UTC 日统计当前租户层里某个动作的操作记录数；没有记录的日子不出现在结果里。</summary>
    /// <param name="action">动作码。</param>
    /// <param name="start">起始时刻（含），UTC。</param>
    /// <param name="end">结束时刻（不含），UTC。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyDictionary<DateOnly, int>> CountOperationRecordsByDayAsync(
        string action,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken = default);

    /// <summary>统计当前租户层里某个动作在区间内涉及的不同目标数。</summary>
    /// <param name="action">动作码。</param>
    /// <param name="start">起始时刻（含），UTC。</param>
    /// <param name="end">结束时刻（不含），UTC。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<int> CountOperationRecordTargetsAsync(
        string action,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken = default);
#endif
#if (IncludeMultiTenancy)

    /// <summary>统计已停用、未删除的租户数。租户注册表在宿主控制库，结果与当前租户上下文无关。</summary>
    Task<int> CountInactiveTenantsAsync(CancellationToken cancellationToken = default);
#endif
}
