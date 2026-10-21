namespace Leistd.Settings.Exceptions;

/// <summary>在宿主上下文之外读写进程级设置。</summary>
/// <remarks>
/// 进程级设置只有宿主那一行，租户上下文下不可达，因此抛出而不是回落到代码默认值。
/// 租户请求里需要进程级配置时，读进程内的运行期状态（如经 <c>AddHostSettings</c> 绑定的 Options）。
/// </remarks>
/// <param name="settingName">被访问的设置名；存储层抛出时为 <see langword="null"/>（那里只知道层级）。</param>
/// <param name="tenantId">当前租户标识；取不到时为 <see langword="null"/>。</param>
public sealed class HostScopeUnavailableException(string? settingName = null, string? tenantId = null)
    : InvalidOperationException(
        (settingName is null
            ? "Host-scoped settings can only be read or written in the host context"
            : $"Setting '{settingName}' is host-scoped and can only be read or written in the host context")
        + (tenantId is null ? "." : $"; current tenant is '{tenantId}'."))
{
    /// <summary>被访问的设置名；由存储层抛出时为 <see langword="null"/>。</summary>
    public string? SettingName { get; } = settingName;
}
