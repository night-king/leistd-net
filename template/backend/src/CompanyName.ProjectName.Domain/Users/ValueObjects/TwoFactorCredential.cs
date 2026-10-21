#if (LocalIdentity)
using Leistd.Ddd.Domain.Values;

namespace CompanyName.ProjectName.Domain.Users.ValueObjects;

/// <summary>已启用的两步验证凭据：加密后的密钥、剩余恢复码摘要与最近一次通过的验证码步序号。</summary>
/// <remarks>用户上它为 null 即未启用；启用、停用都是整体替换，不会留下"已启用但没有密钥"的中间状态。</remarks>
public sealed class TwoFactorCredential : ValueObject
{
    private const char RecoveryCodeSeparator = ';';

    /// <summary>密钥（经 <c>TwoFactorDomainService</c> 用 Data Protection 加密）。</summary>
    public string Secret { get; private set; }

    /// <summary>尚未使用的恢复码摘要，以 <c>;</c> 分隔；用完为空串。</summary>
    /// <remarks>一个用户至多十个、用过即删，不值得为它单开一张表。</remarks>
    public string RecoveryCodes { get; private set; }

    /// <summary>最近一次校验通过的步序号；不大于它的步一律拒绝，防止同一个码被重放。</summary>
    public long LastUsedStep { get; private set; }

    private TwoFactorCredential()
    {
        Secret = null!;
        RecoveryCodes = null!;
    }

    private TwoFactorCredential(string secret, string recoveryCodes, long lastUsedStep)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        Secret = secret;
        RecoveryCodes = recoveryCodes;
        LastUsedStep = lastUsedStep;
    }

    /// <param name="protectedSecret">已加密的密钥。</param>
    /// <param name="recoveryCodeHashes">恢复码摘要。</param>
    /// <param name="usedStep">启用时校验通过的那一步，随即记为已用：同一个码不能紧接着再拿去登录。</param>
    public static TwoFactorCredential Create(string protectedSecret, IEnumerable<string> recoveryCodeHashes, long usedStep) =>
        new(protectedSecret, Join(recoveryCodeHashes), usedStep);

    /// <summary>剩余可用的恢复码个数。</summary>
    public int RecoveryCodesLeft => SplitRecoveryCodes().Count;

    /// <summary>换一组恢复码，旧的全部作废。</summary>
    public TwoFactorCredential WithRecoveryCodes(IEnumerable<string> recoveryCodeHashes) =>
        new(Secret, Join(recoveryCodeHashes), LastUsedStep);

    /// <summary>记下校验通过的步序号。</summary>
    public TwoFactorCredential WithLastUsedStep(long step) => new(Secret, RecoveryCodes, step);

    /// <summary>用掉一个恢复码；摘要不在剩余列表里时返回 null。</summary>
    public TwoFactorCredential? ConsumeRecoveryCode(string hash)
    {
        var codes = SplitRecoveryCodes();
        return codes.Remove(hash) ? new TwoFactorCredential(Secret, Join(codes), LastUsedStep) : null;
    }

    private List<string> SplitRecoveryCodes() =>
        [.. RecoveryCodes.Split(RecoveryCodeSeparator, StringSplitOptions.RemoveEmptyEntries)];

    private static string Join(IEnumerable<string> hashes) => string.Join(RecoveryCodeSeparator, hashes);

    protected override IEnumerable<object?> GetAtomicValues()
    {
        yield return Secret;
        yield return RecoveryCodes;
        yield return LastUsedStep;
    }
}
#endif
