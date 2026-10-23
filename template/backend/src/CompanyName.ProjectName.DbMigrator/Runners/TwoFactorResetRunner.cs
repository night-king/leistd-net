#if (LocalIdentity)
using System.Security.Claims;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Domain.Auth.DomainServices;
using CompanyName.ProjectName.Domain.Users.Repositories;
using Leistd.AmbientContext;
#if (IncludeMultiTenancy)
using Leistd.Data.Connections;
#endif
using Leistd.Lock.Abstractions;
#if (IncludeMultiTenancy)
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Context;
#endif
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.UnitOfWork;

namespace CompanyName.ProjectName.DbMigrator.Runners;

/// <summary>两步验证的部署级重置：本人丢了验证器、恢复码也用尽，在线又没有人能替他重置时，由部署方执行。</summary>
/// <remarks>
/// <para>在线的路径先走：有恢复码的本人用恢复码登录后自助换绑；其他用户由管理员在用户管理里重置。
/// 这条命令兜底的是在线走不通的那一类——典型是内置超级管理员（只能由本人操作，管理员不能重置自己），
/// 以及租户里唯一能管理用户的那个人。</para>
/// <para>效果与管理员重置相同：停用两步验证、清掉密钥与恢复码、换发安全版本，并结束该用户全部登录会话；
/// 在该用户的凭据锁内、一个工作单元里完成，与成功记录同生共死。本人随后用口令登录并重新设置
/// （组织要求两步验证时登录后即被带去设置）。</para>
/// <para>撤销的会话经事务提交后的事件作废校验缓存：与 API 共享 Redis 时立即生效，不共享时 API 最多滞后一个校验间隔
/// （<c>UserSession.TouchInterval</c>）。</para>
/// <para>边界：已签发给应用的访问令牌不在这里撤销，按其有效期自然过期——令牌撤销依赖只在 API 里组合的令牌服务，
/// 部署作业不组合它。这条命令用于本人找回，不用于账号失窃的处置；失窃时先在线停用账号。
/// 安全提醒同理只在 API 里发送，这里不发。</para>
/// </remarks>
internal sealed class TwoFactorResetRunner(
#if (IncludeMultiTenancy)
    ICurrentTenant currentTenant,
    IConnectionStringResolver connectionResolver,
    ITenantConnectionConfigurationStore tenantConnections,
#endif
    IAmbientContext ambientContext,
    IUnitOfWorkManager unitOfWorkManager,
    IDistributedLock distributedLock,
    IUserRepository userRepository,
    UserSessionDomainService userSessionDomainService,
    IOperationRecorder recorder)
{
    /// <param name="userId">要重置的用户 Id。</param>
    /// <param name="tenantId">用户所在租户；宿主用户为 null。没有多租户的项目只有宿主。</param>
    /// <param name="apply">false 只预演，不写库也不留记录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<string> RunAsync(Guid userId, Guid? tenantId, bool apply, CancellationToken cancellationToken = default)
    {
#if (IncludeMultiTenancy)
        if (tenantId is { } id)
        {
            var lookup = await tenantConnections.FindAsync(id, ConnectionStringNames.Default, cancellationToken)
                ?? throw new InvalidOperationException($"Tenant '{id}' does not exist.");
            if (lookup.TenantId != id)
                throw new InvalidOperationException("The control plane returned a different tenant.");
        }

        using var tenantScope = currentTenant.Change(tenantId);
        // 先解析实际落点；不会打印含凭据的连接串。独立库的连接串解不开时在这里失败，不会落到共享库上
        _ = await connectionResolver.ResolveAsync(ConnectionStringNames.Default, cancellationToken);
#endif
        using var actorScope = ambientContext.Begin(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", $"deployment:{Environment.UserName}@{Environment.MachineName}")], "deployment")),
            correlationId: Guid.NewGuid().ToString("N"));

        await using var lockHandle = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lockHandle.LockLost);
        cancellationToken = lockScope.Token;

        using var uow = unitOfWorkManager.Begin(requiresNew: true);
        var where = tenantId is { } tenant ? $"tenant {tenant}" : "the host";
        var user = await userRepository.GetByIdAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException($"User '{userId}' does not exist in {where}. Nothing changed.");

        var target = $"'{user.Username}' ({user.Id}) of {where}";
        if (!user.HasSecondFactor)
            return $"Two-factor authentication is not turned on for {target}; nothing to reset.";

        if (!apply)
            return $"Dry run for {target}: would turn off two-factor authentication (authenticator, recovery codes "
                + "and email codes) and sign out all of its sessions. No data or audit record changed.";

        user.ResetSecondFactors();
        await userRepository.UpdateAsync(user, cancellationToken);
        await userSessionDomainService.RevokeAllAsync(user.Id, exceptSessionId: null, cancellationToken);
        await recorder.RecordSucceededAsync(OperationRecordActions.UserTwoFactorReset,
            OperationTarget.For(user.Id, user.Username), OperationRecordAuthorizations.DeploymentBootstrap, cancellationToken);
        await uow.CompleteAsync(cancellationToken);

        return $"Reset two-factor authentication for {target} and signed out its sessions. "
            + "The user signs in with the password and sets it up again. "
            + "Access tokens already issued to applications are not revoked and expire on their own.";
    }
}
#endif
