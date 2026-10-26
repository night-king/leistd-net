#if (RemoteTokenAuth)
namespace CompanyName.ProjectName.Domain.Users.Abstractions;

/// <summary>三项账户显示偏好；某项为 <see langword="null"/> 表示没有值。</summary>
public sealed record AccountPreferenceValues
{
    public string? Language { get; init; }

    public string? TimeZone { get; init; }

    public string? Theme { get; init; }
}
#endif
