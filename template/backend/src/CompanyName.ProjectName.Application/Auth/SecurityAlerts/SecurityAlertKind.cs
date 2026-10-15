#if (LocalIdentity)
namespace CompanyName.ProjectName.Application.Auth.SecurityAlerts;

/// <summary>安全提醒的种类。</summary>
public enum SecurityAlertKind
{
    /// <summary>账号在一台没见过的设备上登录。</summary>
    NewDeviceSignIn,

    /// <summary>本人修改了密码。</summary>
    PasswordChanged,

    /// <summary>管理员重置了密码。</summary>
    PasswordReset,

    /// <summary>启用了两步验证。</summary>
    TwoFactorEnabled,

    /// <summary>换绑了身份验证器。</summary>
    TwoFactorAuthenticatorReplaced,

    /// <summary>停用了两步验证。</summary>
    TwoFactorDisabled,

    /// <summary>管理员重置了两步验证。</summary>
    TwoFactorReset,

    /// <summary>连续登录失败，账号被临时锁定。</summary>
    LockedOut
}
#endif
