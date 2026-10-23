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
    LockedOut,

    /// <summary>账号的邮箱换成了另一个地址（本人或管理员修改）。</summary>
    EmailChanged,
#if (Email)

    /// <summary>本人凭邮箱验证码找回了口令（忘记口令）。</summary>
    PasswordRecovered,

    /// <summary>开启了邮箱验证码作为登录第二步。</summary>
    TwoFactorEmailEnabled,

    /// <summary>关闭了邮箱验证码。</summary>
    TwoFactorEmailDisabled,
#endif
}
#endif
