#if (LocalIdentity)
namespace CompanyName.ProjectName.Application.Auth.SecurityAlerts;

/// <summary>一条安全提醒。</summary>
/// <param name="Kind">种类。</param>
/// <param name="IpAddress">相关请求的 IP（新设备登录）。</param>
/// <param name="UserAgent">相关请求的 User-Agent（新设备登录）。</param>
/// <param name="Until">锁定截止时刻（账号被锁定）。</param>
public sealed record SecurityAlert(
    SecurityAlertKind Kind,
    string? IpAddress = null,
    string? UserAgent = null,
    DateTime? Until = null);
#endif
