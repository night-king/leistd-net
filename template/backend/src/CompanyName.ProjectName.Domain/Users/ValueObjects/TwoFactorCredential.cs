#if (LocalIdentity)
using Leistd.Ddd.Domain.Values;

namespace CompanyName.ProjectName.Domain.Users.ValueObjects;

/// <summary>已启用的身份验证器：加密后的密钥与最近一次通过的验证码步序号。</summary>
/// <remarks>
/// 用户上它为 null 即未启用；启用、停用都是整体替换，不会留下"已启用但没有密钥"的中间状态。
/// 恢复码不属于它，是账号级的（<see cref="RecoveryCodeSet"/>）：只有通行密钥的人也有恢复码。
/// </remarks>
public sealed class TwoFactorCredential : ValueObject
{
    /// <summary>密钥（经 <c>TwoFactorDomainService</c> 用 Data Protection 加密）。</summary>
    public string Secret { get; private set; }

    /// <summary>最近一次校验通过的步序号；不大于它的步一律拒绝，防止同一个码被重放。</summary>
    public long LastUsedStep { get; private set; }

    private TwoFactorCredential()
    {
        Secret = null!;
    }

    private TwoFactorCredential(string secret, long lastUsedStep)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        Secret = secret;
        LastUsedStep = lastUsedStep;
    }

    /// <param name="protectedSecret">已加密的密钥。</param>
    /// <param name="usedStep">启用时校验通过的那一步，随即记为已用：同一个码不能紧接着再拿去登录。</param>
    public static TwoFactorCredential Create(string protectedSecret, long usedStep) => new(protectedSecret, usedStep);

    /// <summary>记下校验通过的步序号。</summary>
    public TwoFactorCredential WithLastUsedStep(long step) => new(Secret, step);

    protected override IEnumerable<object?> GetAtomicValues()
    {
        yield return Secret;
        yield return LastUsedStep;
    }
}
#endif
