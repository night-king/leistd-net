namespace CompanyName.ProjectName.Application.Settings.Dtos;

/// <summary>账户偏好的完整表示：生效值与个人覆盖原值总是成对给出。</summary>
/// <remarks>
/// 生效值由偏好的持有方（Identity 或 Standalone）按 个人 → 组织 → 部署基线 计算，资源服务原样转发，不在本地重算。
/// 生效值的某项为 <see langword="null"/> 表示三层都没有值：前端按设备回落，服务端按各调用点现有的回落（时区为 UTC）。
/// </remarks>
public sealed record AccountPreferencesOutputDto
{
    /// <summary>生效值。</summary>
    public required AccountPreferenceValuesDto Preferences { get; init; }

    /// <summary>个人覆盖原值；某项为 <see langword="null"/> 表示本人没有设置，继承组织或部署基线。</summary>
    public required AccountPreferenceValuesDto PreferenceOverrides { get; init; }
}
