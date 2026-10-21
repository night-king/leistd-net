#if (LocalIdentity)
using System.Security.Cryptography;
using CompanyName.ProjectName.Domain.Users.Entities;
using Microsoft.AspNetCore.DataProtection;
using Leistd.Security.OneTimeCodes;

namespace CompanyName.ProjectName.Domain.Users.DomainServices;

/// <summary>两步验证密钥的保护，以及按用户已启用的两步验证校验验证码或恢复码。</summary>
/// <remarks>
/// <para>TOTP 密钥必须能还原（每次校验都要用它算码），所以只能加密、不能哈希：用宿主的 Data Protection
/// 加密后落库，数据库单独泄漏时拿不到可用的密钥。加密与解密都在这里，用途字符串只有一处。</para>
/// <para>校验通过会改动用户（记下用掉的步、删掉用掉的恢复码），调用方负责保存。</para>
/// </remarks>
public sealed class TwoFactorDomainService(IDataProtectionProvider dataProtectionProvider)
{
    // 用途字符串固定：改它等于让已存的密钥全部不可解。按官方用法在构造时创建一次、之后复用
    private readonly IDataProtector _protector =
        dataProtectionProvider.CreateProtector("CompanyName.ProjectName.TwoFactorSecret.v1");

    /// <summary>加密一个新生成的密钥，返回可存库（或暂存缓存）的文本。</summary>
    public string ProtectSecret(byte[] secret) => Convert.ToBase64String(_protector.Protect(secret));

    /// <summary>校验身份验证器应用上的验证码。同一个码（同一步）只认一次。</summary>
    public bool VerifyCode(User user, string code, DateTime now)
    {
        if (user.TwoFactor is not { } credential || UnprotectSecret(credential.Secret) is not { } secret)
            return false;

        if (Totp.Verify(secret, code, new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)), credential.LastUsedStep) is not { } step)
            return false;

        user.RecordTwoFactorStep(step);
        return true;
    }

    /// <summary>用掉一个恢复码。</summary>
    public bool UseRecoveryCode(User user, string recoveryCode) =>
        user.TryConsumeRecoveryCode(RecoveryCodes.Hash(recoveryCode));

    /// <summary>校验一个待启用的密钥：启用前必须证明应用里已经添加成功，否则启用后本人就登不进来了。</summary>
    /// <returns>校验通过的步序号；不通过为 null。</returns>
    public long? VerifySetupCode(string protectedSecret, string code, DateTime now) =>
        UnprotectSecret(protectedSecret) is { } secret ? Totp.Verify(secret, code, new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)), null) : null;

    // 密文损坏或密钥环已不可用时视同"验证码不对"：该用户需由管理员重置两步验证，而不是让登录报 500
    private byte[]? UnprotectSecret(string protectedSecret)
    {
        try
        {
            return _protector.Unprotect(Convert.FromBase64String(protectedSecret));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
#endif
