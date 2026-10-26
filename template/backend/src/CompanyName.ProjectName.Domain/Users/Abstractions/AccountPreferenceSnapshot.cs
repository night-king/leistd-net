#if (RemoteTokenAuth)
namespace CompanyName.ProjectName.Domain.Users.Abstractions;

/// <summary>Identity 给出的账户偏好完整表示：生效值与个人覆盖原值。</summary>
public sealed record AccountPreferenceSnapshot
{
    /// <summary>生效值（个人 → 组织 → 部署基线，由 Identity 计算）。</summary>
    public required AccountPreferenceValues Preferences { get; init; }

    /// <summary>本人的覆盖原值。</summary>
    public required AccountPreferenceValues PreferenceOverrides { get; init; }
}
#endif
