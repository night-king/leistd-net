#if (LocalIdentity)
namespace CompanyName.ProjectName.Domain.Users.ValueObjects;

/// <summary>登录第二步的验证方式。</summary>
/// <remarks>
/// 首选方式按值落库，成员取显式值，不随声明顺序变化。恢复码只作备选：它不能设为首选，也不会被选作默认方式。
/// </remarks>
public enum TwoFactorMethod
{
    /// <summary>身份验证器应用上的验证码；唯一满足组织"必须启用两步验证"要求的方式。</summary>
    Authenticator = 1,

    /// <summary>发到已验证邮箱的验证码。</summary>
    Email = 2,

    /// <summary>启用身份验证器时发下的恢复码。</summary>
    RecoveryCode = 3
}
#endif
