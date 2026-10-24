#if (LocalIdentity)
namespace CompanyName.ProjectName.Domain.Users.ValueObjects;

/// <summary>登录第二步的验证方式。</summary>
/// <remarks>
/// <para>首选方式按值落库，成员取显式值，不随声明顺序变化。恢复码只作备选：它不能设为首选，也不会被选作默认方式。</para>
/// <para>新增方式时在 <see cref="TwoFactorMethods.IsIndependent"/> 里声明它是不是独立因子。</para>
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

/// <summary>验证方式的属性。</summary>
public static class TwoFactorMethods
{
    /// <summary>是不是独立因子：证明的东西不经过邮箱，拿下邮箱的人拿不到它。</summary>
    /// <remarks>
    /// 邮箱验证码不是：找回口令走的也是这个邮箱，能进邮箱的人同时拿到了口令和第二步。
    /// 第一步经邮箱通道得来（邮件找回的口令、按邮箱自动关联的外部登录）或组织要求两步验证时，第二步只认独立因子。
    /// </remarks>
    public static bool IsIndependent(this TwoFactorMethod method) =>
        method is TwoFactorMethod.Authenticator or TwoFactorMethod.RecoveryCode;
}
#endif
