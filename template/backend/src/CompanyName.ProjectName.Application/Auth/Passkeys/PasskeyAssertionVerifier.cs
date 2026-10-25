#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.DomainServices;
using CompanyName.ProjectName.Domain.Auth.Entities;
using CompanyName.ProjectName.Domain.Auth.Passkeys;
using Leistd.Timing;
using Microsoft.Extensions.Logging;

namespace CompanyName.ProjectName.Application.Auth.Passkeys;

/// <summary>用通行密钥证明是本人：登录第二步、免口令登录与确认身份共用的断言校验。</summary>
/// <remarks>
/// 先按响应里的凭据 Id 找到已登记的通行密钥，再用它校验断言；通过后记下新的计数器与备份状态。
/// 找不到、不属于预期的用户、协议校验不通过都只返回失败，原因只进日志：回给浏览器的一律是"验证没有通过"。
/// </remarks>
internal sealed class PasskeyAssertionVerifier(
    IPasskeyProtocol protocol,
    PasskeyDomainService passkeyDomainService,
    IClock clock,
    ILogger<PasskeyAssertionVerifier> logger)
{
    /// <summary>为这些通行密钥生成断言选项；不给时让浏览器列出本站的可发现凭据（免口令登录）。</summary>
    public PasskeyCeremony Begin(IEnumerable<UserPasskey> allowed) =>
        protocol.BeginAssertion(allowed.Select(p => new PasskeyDescriptor(p.CredentialId, p.GetTransports())));

    /// <summary>响应声明的是哪一把已登记的通行密钥（当前租户内）；认不出时返回 null。</summary>
    public async Task<UserPasskey?> FindAsync(string response, CancellationToken cancellationToken)
    {
        if (protocol.ReadAssertionIdentity(response) is not { } identity)
            return null;

        var passkey = await passkeyDomainService.FindByCredentialIdAsync(identity.CredentialId, cancellationToken);
        // 可发现凭据带回用户句柄：与登记时写进去的不一致，就不是这把凭据的主人在用
        return passkey is not null && (identity.UserHandle is null || identity.UserHandle.AsSpan().SequenceEqual(passkey.UserHandle))
            ? passkey
            : null;
    }

    /// <summary>用 <paramref name="passkey"/> 校验断言；通过时记下使用并返回 true。</summary>
    public async Task<bool> VerifyAsync(UserPasskey passkey, string state, string response, CancellationToken cancellationToken)
    {
        try
        {
            var assertion = await protocol.CompleteAssertionAsync(
                state,
                response,
                new PasskeyStoredKey(passkey.CredentialId, passkey.PublicKey, passkey.SignCount, passkey.UserHandle),
                cancellationToken);
            await passkeyDomainService.RecordUseAsync(passkey, assertion.SignCount, assertion.IsBackedUp, clock.Now, cancellationToken);
            return true;
        }
        catch (PasskeyVerificationException exception)
        {
            // 计数器回退也在这里：可能是凭据被克隆，记下凭据以便排查，不自动停用（误报时本人会被锁在门外）
            logger.LogWarning(exception, "Passkey {PasskeyId} of user {UserId} failed verification", passkey.Id, passkey.UserId);
            return false;
        }
    }
}
#endif
