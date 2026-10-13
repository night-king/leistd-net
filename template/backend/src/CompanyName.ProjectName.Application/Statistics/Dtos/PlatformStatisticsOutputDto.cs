namespace CompanyName.ProjectName.Application.Statistics.Dtos;

/// <summary>平台统计输出 DTO：仪表盘一次取齐的全部数据，范围是当前上下文（宿主或本租户）。</summary>
/// <remarks>本项目形态取不到的指标不出现在 DTO 上，而不是给 0。</remarks>
public record PlatformStatisticsOutputDto
{
    /// <summary>统计区间的天数（7 或 30）。</summary>
    public required int Days { get; init; }

    /// <summary>用户总数：本区间值为此刻现存（未删除）的用户数；上一区间值为本区间开始时刻存在的用户数。</summary>
    /// <remarks>开始时刻存在指创建于其前、且当时尚未删除；之后才删除的用户仍计入上一区间值。</remarks>
    public required StatisticValueOutputDto TotalUsers { get; init; }

    /// <summary>区间内注册的用户数，按创建事件计：注册后又被删除的用户仍计入。</summary>
    public required StatisticValueOutputDto NewUsers { get; init; }
#if (LocalIdentity)

#if (IncludeOperationRecords)
    /// <summary>活跃用户：区间内有登录成功记录的不同用户数。</summary>
#else
    /// <summary>活跃用户：最近一次登录落在本区间内的用户数。只有当前值，没有上一区间值。</summary>
#endif
    public required StatisticValueOutputDto ActiveUsers { get; init; }
#endif
#if (LocalIdentity && IncludeOperationRecords)

    /// <summary>区间内的登录失败次数。</summary>
    /// <remarks>匿名的登录失败按被尝试的标识节流后才落记录，所以是近似值，宜看趋势、不宜当精确计数。</remarks>
    public required StatisticValueOutputDto FailedLogins { get; init; }

    /// <summary>本区间每个 UTC 日的登录成功与失败次数，按日期升序，共 <see cref="Days"/> 项，没有记录的日子为 0。</summary>
    public required IReadOnlyList<LoginTrendPointOutputDto> LoginTrend { get; init; }
#endif

    /// <summary>需要管理员关注的当前状态，不分区间。</summary>
    public required PlatformAttentionOutputDto Attention { get; init; }
}

/// <summary>一项统计的本区间值与上一区间值。</summary>
public record StatisticValueOutputDto
{
    /// <summary>本区间的值。</summary>
    public required int Value { get; init; }

    /// <summary>上一区间的值；没有可比的数据来源时为 null。上一区间没有数据时为 0。</summary>
    public int? Previous { get; init; }
}
#if (LocalIdentity && IncludeOperationRecords)

/// <summary>某一 UTC 日的登录次数。</summary>
public record LoginTrendPointOutputDto
{
    /// <summary>日期（UTC）。</summary>
    public required DateOnly Date { get; init; }

    /// <summary>登录成功次数。</summary>
    public required int Succeeded { get; init; }

    /// <summary>登录失败次数（近似值，见 <see cref="PlatformStatisticsOutputDto.FailedLogins"/>）。</summary>
    public required int Failed { get; init; }
}
#endif

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
