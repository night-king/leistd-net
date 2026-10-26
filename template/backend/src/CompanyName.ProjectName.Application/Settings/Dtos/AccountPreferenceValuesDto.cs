namespace CompanyName.ProjectName.Application.Settings.Dtos;

/// <summary>三项账户显示偏好。</summary>
public sealed record AccountPreferenceValuesDto
{
    /// <summary>界面语言（culture 名，如 <c>zh-CN</c>）；未启用多语言的项目恒为 <see langword="null"/>。</summary>
    public string? Language { get; init; }

    /// <summary>展示时区（IANA 名）。</summary>
    public string? TimeZone { get; init; }

    /// <summary>外观：<c>system</c>、<c>light</c> 或 <c>dark</c>。</summary>
    public string? Theme { get; init; }
}
