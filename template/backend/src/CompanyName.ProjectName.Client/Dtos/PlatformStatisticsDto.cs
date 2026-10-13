namespace CompanyName.ProjectName.Client.Dtos;

/// <summary>平台统计（对应 <c>GET /api/v1/platform/statistics</c>），范围是调用身份所在的宿主或租户。</summary>
/// <remarks>各指标的 <see cref="StatisticValueDto.Series"/> 与 <see cref="Buckets"/> 按下标对齐。</remarks>
public sealed record PlatformStatisticsDto
{
    /// <summary>本次统计实际采用的区间、时区与粒度。</summary>
    public required StatisticsRangeDto Range { get; init; }

    /// <summary>各统计桶起点的 UTC 时刻，升序；最后一个桶止于 <see cref="StatisticsRangeDto.End"/>，区间含今天时截到此刻。</summary>
    public required IReadOnlyList<DateTime> Buckets { get; init; }

    /// <summary>用户总数：期末存量；上期值是上一区间终点前的存量（含之后才删除的）。</summary>
    public required StatisticValueDto TotalUsers { get; init; }

    /// <summary>区间内注册的用户数，注册后又被删除的仍计入。</summary>
    public required StatisticValueDto NewUsers { get; init; }
#if (LocalIdentity)

    /// <summary>区间内登录过的不同用户数；没有登录记录时只有本期值。</summary>
    public required StatisticValueDto ActiveUsers { get; init; }
#endif
#if (LocalIdentity && IncludeOperationRecords)

    /// <summary>区间内的登录成功次数。</summary>
    public required StatisticValueDto SucceededLogins { get; init; }

    /// <summary>区间内的登录失败次数（近似值）。</summary>
    public required StatisticValueDto FailedLogins { get; init; }
#endif

    /// <summary>需要管理员关注的当前状态。</summary>
    public required PlatformAttentionDto Attention { get; init; }
}

/// <summary>统计实际采用的区间。</summary>
/// <param name="StartDate">起始日（含）</param>
/// <param name="EndDate">结束日（含）</param>
/// <param name="TimeZone">解释日期与切分统计桶所用的时区（IANA 名）</param>
/// <param name="Granularity">统计桶的粒度</param>
/// <param name="Start">本区间起点（含），UTC</param>
/// <param name="End">本区间终点（不含），UTC</param>
/// <param name="PreviousStart">上一区间起点（含），UTC</param>
/// <param name="PreviousEnd">上一区间终点（不含），UTC</param>
public sealed record StatisticsRangeDto(
    DateOnly StartDate,
    DateOnly EndDate,
    string TimeZone,
    StatisticsGranularity Granularity,
    DateTime Start,
    DateTime End,
    DateTime PreviousStart,
    DateTime PreviousEnd);

/// <summary>统计桶的粒度。</summary>
public enum StatisticsGranularity
{
    /// <summary>按小时：单日区间。</summary>
    Hour,

    /// <summary>按日：多日区间。</summary>
    Day
}

/// <summary>一项统计的本期值、上期值与按桶序列。</summary>
/// <param name="Value">本区间的值</param>
/// <param name="Previous">上一区间的值；没有可比的数据来源时为 null</param>
/// <param name="Series">本区间按桶的值；没有按桶的数据来源时为 null</param>
public sealed record StatisticValueDto(int Value, int? Previous, IReadOnlyList<int>? Series);

/// <summary>需要管理员关注的当前状态。</summary>
public sealed record PlatformAttentionDto
{
#if (LocalIdentity)
    /// <summary>此刻处于锁定中的用户数。</summary>
    public int LockedUsers { get; init; }

    /// <summary>持有管理员角色、尚未启用两步验证的用户数。</summary>
    public int AdminsWithoutTwoFactor { get; init; }

    /// <summary>邮箱尚未验证的用户数。</summary>
    public int UnconfirmedEmailUsers { get; init; }
#endif
#if (LocalIdentity && IncludeMultiTenancy)

    /// <summary>已停用的租户数；租户身份调用时为 null。</summary>
    public int? DisabledTenants { get; init; }
#endif
}
