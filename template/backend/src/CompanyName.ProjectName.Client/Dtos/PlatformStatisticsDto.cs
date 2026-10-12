namespace CompanyName.ProjectName.Client.Dtos;

/// <summary>平台统计（对应 <c>GET /api/v1/platform/statistics</c>），范围是调用身份所在的宿主或租户。</summary>
public sealed record PlatformStatisticsDto
{
    /// <summary>统计区间的天数（7 或 30）。</summary>
    public required int Days { get; init; }

    /// <summary>用户总数；上一区间值为上一区间结束时的总数。</summary>
    public required StatisticValueDto TotalUsers { get; init; }

    /// <summary>区间内新建的用户数。</summary>
    public required StatisticValueDto NewUsers { get; init; }
#if (LocalIdentity)

    /// <summary>区间内登录过的不同用户数。</summary>
    public required StatisticValueDto ActiveUsers { get; init; }
#endif
#if (LocalIdentity && IncludeOperationRecords)

    /// <summary>区间内的登录失败次数（近似值）。</summary>
    public required StatisticValueDto FailedLogins { get; init; }

    /// <summary>本区间每个 UTC 日的登录次数，按日期升序。</summary>
    public required IReadOnlyList<LoginTrendPointDto> LoginTrend { get; init; }
#endif

    /// <summary>需要管理员关注的当前状态。</summary>
    public required PlatformAttentionDto Attention { get; init; }
}

/// <summary>一项统计的本区间值与上一区间值。</summary>
/// <param name="Value">本区间的值</param>
/// <param name="Previous">上一区间的值；没有可比的数据来源时为 null</param>
public sealed record StatisticValueDto(int Value, int? Previous);
#if (LocalIdentity && IncludeOperationRecords)

/// <summary>某一 UTC 日的登录次数。</summary>
/// <param name="Date">日期（UTC）</param>
/// <param name="Succeeded">登录成功次数</param>
/// <param name="Failed">登录失败次数（近似值）</param>
public sealed record LoginTrendPointDto(DateOnly Date, int Succeeded, int Failed);
#endif

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
