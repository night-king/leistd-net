namespace CompanyName.ProjectName.Application.Statistics.Dtos;

/// <summary>平台统计输出 DTO：仪表盘一次取齐的全部数据，范围是当前上下文（宿主或本租户）。</summary>
/// <remarks>
/// <para>本项目形态取不到的指标不出现在 DTO 上，而不是给 0。</para>
/// <para>各指标的 <see cref="StatisticValueOutputDto.Series"/> 与 <see cref="Buckets"/> 按下标对齐。</para>
/// </remarks>
public record PlatformStatisticsOutputDto
{
    /// <summary>本次统计实际采用的区间、时区与粒度。</summary>
    public required StatisticsRangeOutputDto Range { get; init; }

    /// <summary>各统计桶起点的 UTC 时刻，升序；最后一个桶止于 <see cref="StatisticsRangeOutputDto.End"/>。</summary>
    /// <remarks>只含与本区间实际范围相交的桶：区间含今天时末桶截到此刻，不为还没到的时段补 0；零长度区间为空。</remarks>
    public required IReadOnlyList<DateTime> Buckets { get; init; }

    /// <summary>用户总数：期末的存量。</summary>
    /// <remarks>
    /// 本期值是本区间实际终点前存在的用户数，上期值是上一区间终点前的；存在指创建于其前、且当时尚未删除，
    /// 之后才删除的照样计入。序列是各桶终点前的存量，不能按周相加。
    /// </remarks>
    public required StatisticValueOutputDto TotalUsers { get; init; }

    /// <summary>区间内注册的用户数，按创建事件计：注册后又被删除的用户仍计入。序列可按周相加。</summary>
    public required StatisticValueOutputDto NewUsers { get; init; }
#if (LocalIdentity)

#if (IncludeOperationRecords)
    /// <summary>活跃用户：区间内有登录成功记录的不同用户数。</summary>
    /// <remarks>序列是各桶内分别去重的人数，桶数相加不等于区间人数，不能按周相加。</remarks>
#else
    /// <summary>活跃用户：最近一次登录落在本区间内的现存用户数。</summary>
    /// <remarks>
    /// 没有登录记录可查，只能看用户行上的最近登录时间：它只保留最后一次，答不出历史区间里有多少人登录过，
    /// 所以只有本期值，没有上期值与序列；看历史区间时只是近似。
    /// </remarks>
#endif
    public required StatisticValueOutputDto ActiveUsers { get; init; }
#endif
#if (LocalIdentity && IncludeOperationRecords)

    /// <summary>区间内的登录成功次数；序列可按周相加。</summary>
    public required StatisticValueOutputDto SucceededLogins { get; init; }

    /// <summary>区间内的登录失败次数；序列可按周相加。</summary>
    /// <remarks>匿名的登录失败按被尝试的标识节流后才落记录，所以是近似值，宜看趋势、不宜当精确计数。</remarks>
    public required StatisticValueOutputDto FailedLogins { get; init; }
#endif

    /// <summary>需要管理员关注的当前状态，不分区间。</summary>
    public required PlatformAttentionOutputDto Attention { get; init; }
}

/// <summary>统计实际采用的区间：请求的日历区间、解释它的时区，以及换算出的 UTC 范围。</summary>
public record StatisticsRangeOutputDto
{
    /// <summary>起始日（含），回显请求。</summary>
    public required DateOnly StartDate { get; init; }

    /// <summary>结束日（含），回显请求。</summary>
    public required DateOnly EndDate { get; init; }

    /// <summary>解释日期与切分统计桶所用的时区（IANA 名），回显请求；前端按它格式化坐标轴与提示。</summary>
    public required string TimeZone { get; init; }

    /// <summary>统计桶的粒度：单日按小时，其余按日。</summary>
    public required StatisticsGranularity Granularity { get; init; }

    /// <summary>本区间起点（含），UTC：起始日在该时区的第一个时刻。</summary>
    public required DateTime Start { get; init; }

    /// <summary>本区间终点（不含），UTC：结束日次日的第一个时刻，区间含今天时截到此刻。</summary>
    public required DateTime End { get; init; }

    /// <summary>上一区间起点（含），UTC：把本区间按日历前移同样的天数。</summary>
    public required DateTime PreviousStart { get; init; }

    /// <summary>上一区间终点（不含），UTC：与本区间比较相同的本地进度，区间含今天时是前移后的同一钟点。</summary>
    public required DateTime PreviousEnd { get; init; }
}

/// <summary>统计桶的粒度。</summary>
public enum StatisticsGranularity
{
    /// <summary>按小时：单日区间。</summary>
    Hour,

    /// <summary>按日：多日区间；按周由前端把日桶聚合。</summary>
    Day
}

/// <summary>一项统计的本期值、上期值与按桶序列。</summary>
public record StatisticValueOutputDto
{
    /// <summary>本区间的值。</summary>
    public required int Value { get; init; }

    /// <summary>上一区间的值；没有可比的数据来源时为 null。上一区间没有数据时为 0。</summary>
    public int? Previous { get; init; }

    /// <summary>本区间按桶的值，与 <see cref="PlatformStatisticsOutputDto.Buckets"/> 等长；没有按桶的数据来源时为 null。</summary>
    public IReadOnlyList<int>? Series { get; init; }
}

/// <summary>需要管理员关注的当前状态。</summary>
public record PlatformAttentionOutputDto
{
#if (LocalIdentity)
    /// <summary>此刻处于锁定中的用户数（管理员锁定，或未到期的登录失败锁定）。</summary>
    public required int LockedUsers { get; init; }

    /// <summary>持有管理员角色、尚未启用两步验证的用户数。</summary>
    public required int AdminsWithoutTwoFactor { get; init; }

    /// <summary>邮箱尚未验证的用户数。</summary>
    public required int UnconfirmedEmailUsers { get; init; }
#endif
#if (LocalIdentity && IncludeMultiTenancy)

    /// <summary>已停用的租户数；只在宿主上下文统计，租户上下文为 null。</summary>
    public int? DisabledTenants { get; init; }
#endif
}
