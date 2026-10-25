#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.Entities;
using CompanyName.ProjectName.Domain.Auth.Errors;
using CompanyName.ProjectName.Domain.Users.Entities;
using Leistd.Ddd.Domain.Repositories;
using Leistd.ExceptionHandling;

namespace CompanyName.ProjectName.Domain.Auth.DomainServices;

/// <summary>通行密钥的登记、删除与重置：凭据行与用户上的个数、恢复码、安全版本一起变化。</summary>
/// <remarks>
/// 协议校验（证明、断言）不在这里：调用方经应用层的校验端口做完，只把校验通过的凭据数据交进来。
/// 调用方在本人的凭据锁与同一工作单元内调用，凭据行与用户行一起提交。
/// </remarks>
public class PasskeyDomainService(IRepository<UserPasskey, Guid> passkeyRepository)
{
    /// <summary>本人的全部通行密钥，按登记先后。</summary>
    public async Task<List<UserPasskey>> GetListAsync(Guid userId, CancellationToken cancellationToken = default) =>
        [.. (await passkeyRepository.GetListAsync(p => p.UserId == userId, cancellationToken)).OrderBy(p => p.CreationTime)];

    /// <summary>按凭据 Id 找通行密钥（当前租户内）；没有时返回 null。</summary>
    public Task<UserPasskey?> FindByCredentialIdAsync(byte[] credentialId, CancellationToken cancellationToken = default) =>
        passkeyRepository.GetFirstAsync(p => p.CredentialId == credentialId, q => q.OrderBy(p => p.Id), cancellationToken);

    /// <summary>登记一把校验通过的通行密钥。</summary>
    /// <param name="user">本人（已在凭据锁内重读）。</param>
    /// <param name="credential">认证器交回、已通过证明校验的凭据。</param>
    /// <param name="name">名称。</param>
    /// <param name="recoveryCodeHashes">它是第一个独立因子（<c>User.NeedsRecoveryCodes</c>）时发放的恢复码摘要，否则为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="BusinessException">同一凭据已经登记过（<see cref="AuthErrorCodes.PasskeyAlreadyRegistered"/>）。</exception>
    public async Task<UserPasskey> RegisterAsync(
        User user,
        PasskeyCredentialData credential,
        string name,
        IEnumerable<string>? recoveryCodeHashes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        // 浏览器已按排除列表挡住本人的重复登记；这里兜住别处已登记同一凭据的情况，唯一索引是最后一道
        if (await passkeyRepository.AnyAsync(p => p.CredentialId == credential.CredentialId, cancellationToken))
        {
            throw new BusinessException(AuthErrorCodes.PasskeyAlreadyRegistered, "This passkey is already registered.");
        }

        var passkey = await passkeyRepository.InsertAsync(new UserPasskey(user.Id, credential, name), cancellationToken);
        user.RecordPasskeyRegistered(recoveryCodeHashes);
        return passkey;
    }

    /// <summary>本人名下的一把通行密钥；不存在或不属于本人时以 <see cref="AuthErrorCodes.PasskeyNotFound"/> 拒绝。</summary>
    public async Task<UserPasskey> GetOwnAsync(User user, Guid passkeyId, CancellationToken cancellationToken = default)
    {
        var passkey = await passkeyRepository.GetFirstAsync(
            p => p.Id == passkeyId && p.UserId == user.Id, q => q.OrderBy(p => p.Id), cancellationToken);
        return passkey ?? throw new BusinessException(AuthErrorCodes.PasskeyNotFound, "This passkey doesn't exist.");
    }

    /// <summary>给本人的一把通行密钥改名。</summary>
    public async Task<UserPasskey> RenameAsync(User user, Guid passkeyId, string name, CancellationToken cancellationToken = default)
    {
        var passkey = await GetOwnAsync(user, passkeyId, cancellationToken);
        passkey.Rename(name);
        return await passkeyRepository.UpdateAsync(passkey, cancellationToken);
    }

    /// <summary>记下一次通过的使用：新的计数器与备份状态。</summary>
    public async Task RecordUseAsync(UserPasskey passkey, long signCount, bool isBackedUp, DateTime now, CancellationToken cancellationToken = default)
    {
        passkey.RecordUse(signCount, isBackedUp, now);
        await passkeyRepository.UpdateAsync(passkey, cancellationToken);
    }

    /// <summary>删除本人的一把通行密钥；最后一个独立因子去掉时恢复码随之作废（见 <c>User.RecordPasskeyRemoved</c>）。</summary>
    public async Task RemoveAsync(User user, UserPasskey passkey, CancellationToken cancellationToken = default)
    {
        if (passkey.UserId != user.Id)
            throw new InvalidOperationException("The passkey belongs to another user.");

        await passkeyRepository.DeleteAsync(passkey, cancellationToken);
        user.RecordPasskeyRemoved();
    }

    /// <summary>清掉全部第二步方式，含通行密钥：管理员或部署命令重置两步验证时用。</summary>
    public async Task ResetSecondFactorsAsync(User user, CancellationToken cancellationToken = default)
    {
        foreach (var passkey in await passkeyRepository.GetListAsync(p => p.UserId == user.Id, cancellationToken))
        {
            await passkeyRepository.DeleteAsync(passkey, cancellationToken);
        }

        user.ResetSecondFactors();
    }
}
#endif
