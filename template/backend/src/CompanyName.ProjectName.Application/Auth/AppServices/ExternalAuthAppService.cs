#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Users.Errors;
using CompanyName.ProjectName.Application.Auth.SignIn;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Domain.Auth.Errors;
using Leistd.Lock.Abstractions;
using Leistd.UnitOfWork;
using Leistd.ExceptionHandling;
using CompanyName.ProjectName.Application.Auth.Dtos;
using CompanyName.ProjectName.Domain.Auth.Abstractions;
using CompanyName.ProjectName.Domain.Auth.DomainServices;
using CompanyName.ProjectName.Domain.Users.Repositories;
using Leistd.Ddd.Application.AppServices;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Domain.Auth.Entities;
using CompanyName.ProjectName.Domain.Users.Entities;
using Leistd.Ddd.Domain.Repositories;
using Leistd.ObjectMapping.Abstractions;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.Security.Users;

namespace CompanyName.ProjectName.Application.Auth.AppServices;

/// <summary>外部身份验证服务。</summary>
internal sealed class ExternalAuthAppService(
    ExternalAuthDomainService externalAuthDomainService,
    IRoleRepository roleRepository,
    SessionIssuer sessionIssuer,
    IRepository<User, Guid> userRepository,
    IRepository<ExternalLoginConnection, Guid> externalLoginConnectionRepository,
    ICurrentUser currentUser,
    IOperationRecorder operationRecorder,
    IReauthenticationGuard reauthenticationGuard,
    IDistributedLock distributedLock,
    IUnitOfWorkManager unitOfWorkManager,
    IObjectMapper objectMapper) : BaseAppService(), IExternalAuthAppService
{
    /// <summary>处理外部登录回调。</summary>
    /// <remarks>
    /// <para>先只读地认出已有账号（已绑定的连接，或两边都已验证的同一邮箱）。认不出时新建：建用户、分配默认角色、
    /// 建外部登录连接与登记会话在一个工作单元里提交——缺了连接行，同一外部账号下次登录会再建一个用户。</para>
    /// <para>认出已有账号时，在它的凭据锁内按最新状态重新判定（连接还在不在、邮箱是否仍双方已验证）、写入关联、
    /// 判定能否登录并登记会话，一起提交：与改邮箱、启用两步验证、改口令、解绑串行，
    /// 不会凭过时的事实建立关联或登记会话；登录被拒（停用、锁定）时关联随之回滚，不留下一种登录方式。</para>
    /// </remarks>
    public async Task<SessionLoginResult> AuthenticateExternalUserAsync(
        string provider,
        ExternalUserInfo externalUserInfo,
        CancellationToken cancellationToken = default)
    {
        Guid? existingUserId;
        using (var lookup = unitOfWorkManager.Begin(requiresNew: true))
        {
            existingUserId = await externalAuthDomainService.LocateUserAsync(provider, externalUserInfo, cancellationToken);
            await lookup.CompleteAsync(cancellationToken);
        }

        if (existingUserId is null)
        {
            using var unit = unitOfWorkManager.Begin(requiresNew: true);
            var (user, connection, created) = await externalAuthDomainService.FindOrCreateUserAsync(provider, externalUserInfo, cancellationToken);
            if (created)
            {
                // 带出刚分配的角色名：成员关系在本工作单元内尚未落库，回查不到
                var defaultRoles = await roleRepository.GetDefaultRolesAsync(cancellationToken);
                user.AssignRoles(defaultRoles.Select(role => role.Id));
                await userRepository.UpdateAsync(user, cancellationToken);
                var started = await sessionIssuer.StartAsync(
                    user, FirstStepOf(connection), [.. defaultRoles.Select(role => role.Name)], cancellationToken);
                await unit.CompleteAsync(cancellationToken);
                return started;
            }

            // 认出与新建之间别处已建好关联：不在锁外提交，回滚后按已有账号走下面的路径
            existingUserId = user.Id;
        }

        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(existingUserId.Value), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;
        using var locked = unitOfWorkManager.Begin(requiresNew: true);

        var (current, currentConnection, createdNow) = await externalAuthDomainService.FindOrCreateUserAsync(provider, externalUserInfo, cancellationToken);
        if (createdNow || current.Id != existingUserId)
        {
            // 取锁之前关联依据已变（解绑、换了邮箱）：这次外部登录对应的已不是认出的那个账号
            throw new BusinessException(ExternalAuthErrorCodes.InvalidState, "Invalid or expired external authentication intent.");
        }

        var result = await sessionIssuer.StartAsync(current, FirstStepOf(currentConnection), cancellationToken: cancellationToken);
        await locked.CompleteAsync(cancellationToken);
        return result;
    }

    // 按邮箱自动关联的连接凭的是邮箱：第二步只认独立因子，用它完成一次后连接不再带这个标记
    private static SignInFirstStep FirstStepOf(ExternalLoginConnection connection) =>
        new(connection.LinkedByEmail, connection.Id);

    /// <summary>本人的外部账号绑定：部署已配置的每个提供商，附带本人在其下的绑定。</summary>
    public async Task<ExternalLoginsOutputDto> GetCurrentUserExternalLoginsAsync(IEnumerable<string> availableProviders, CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserEntityAsync(cancellationToken);
        var links = (await externalLoginConnectionRepository.GetListAsync(c => c.UserId == user.Id, cancellationToken)).ToList();

        return new ExternalLoginsOutputDto
        {
            HasPassword = user.HasLocalPassword,
            Providers = availableProviders
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => new ExternalLoginProviderOutputDto
                {
                    Provider = p,
                    Link = links
                        .Where(l => string.Equals(l.Provider, p, StringComparison.OrdinalIgnoreCase))
                        .Select(l => objectMapper.Map<ExternalLoginConnection, ExternalLoginLinkOutputDto>(l))
                        .FirstOrDefault()
                })
                .ToList()
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// 预检本来不持本人锁：这里取本人的凭据锁完成确认身份并提交（验证码用掉不退回），随即释放，
    /// 之后调用方才消费外部票据、进入"票据锁 → 本人锁"的绑定流程，不持本人锁跨入后半段。
    /// </remarks>
    public Task EnsureLinkAllowedAsync(CompleteExternalLinkInputDto? input, CancellationToken cancellationToken = default) =>
        reauthenticationGuard.EnsureRecentInOwnLockAsync(
            ReauthenticationProof.Of(input?.Password, input?.TwoFactorCode, input?.Passkey),
            OperationRecordActions.AuthExternalLoginLinked,
            cancellationToken);

    /// <summary>把外部身份绑定到当前用户（外部授权回来后调用）。</summary>
    /// <remarks>
    /// 确认身份由调用方在消费外部票据之前经 <see cref="EnsureLinkAllowedAsync"/> 完成，这里不再判：
    /// 票据已经消费，再拒绝一次就只能让本人重新走外部授权，而界面会误以为带上口令即可重试。
    /// 在本人的凭据锁内读、写并提交，与解绑、外部登录串行。
    /// </remarks>
    public async Task LinkCurrentUserAsync(
        string provider,
        ExternalUserInfo externalUserInfo,
        CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id!.Value;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;
        using var unit = unitOfWorkManager.Begin(requiresNew: true);

        var user = await GetCurrentUserEntityAsync(cancellationToken);
        var link = await externalAuthDomainService.LinkAsync(user, provider, externalUserInfo, cancellationToken);

        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.AuthExternalLoginLinked,
            // 目标名用提供商侧标签：审计要回答"绑定的是哪个外部账号"，本地用户名回答不了这个。
            // 审计按设计显示可公开展示的值（见 operation-records 的 LocalizationData 约定），
            // 与日志口径不同——日志里联系方式要脱敏，审计是既成事实的记录
            OperationTarget.For(link.Id, $"{provider}: {externalUserInfo.ProviderAccountLabel}"),
            OperationRecordAuthorizations.AuthenticatedSelf,
            cancellationToken);
        await unit.CompleteAsync(cancellationToken);
    }

    /// <summary>解绑当前用户的一个外部账号。</summary>
    /// <remarks>
    /// <para>解绑改变"谁能登进这个账号"，与绑定同等敏感：真正删除之前确认是本人。解绑没有外部票据可保留，
    /// 确认身份失败时什么都没改，带上凭证重试同一个请求即可。</para>
    /// <para>不存在或不属于本人时静默成功、也不要求确认身份：解绑是幂等的，也不借此透露别人的绑定 Id 是否存在。
    /// 解绑后不剩登录方式时先于确认身份拒绝。</para>
    /// </remarks>
    // 在本人的凭据锁内：确认身份独立提交（验证码用掉不退回），再另开工作单元删除绑定、轮换安全版本、记录成功，
    // 三者同生共死——只删了绑定而版本没轮换，此前的登录挑战仍能完成；登录第二步在锁内核对安全版本后登记会话，不会夹在中间
    public async Task UnlinkCurrentUserAsync(Guid id, UnlinkExternalLoginInputDto? input, CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id!.Value;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;

        if (await externalLoginConnectionRepository.CountAsync(c => c.Id == id && c.UserId == userId, cancellationToken) == 0)
            return;

        // 注定被拒的解绑（最后一种登录方式）先于确认身份拒绝，不消耗验证码
        await externalAuthDomainService.EnsureAnotherSignInMethodAsync(
            await GetCurrentUserEntityAsync(cancellationToken), id, cancellationToken);

        await reauthenticationGuard.EnsureRecentAsync(
            ReauthenticationProof.Of(input?.Password, input?.TwoFactorCode, input?.Passkey),
            OperationRecordActions.AuthExternalLoginUnlinked,
            cancellationToken);

        using var unit = unitOfWorkManager.Begin(requiresNew: true);
        var user = await GetCurrentUserEntityAsync(cancellationToken);
        var removed = await externalAuthDomainService.UnlinkAsync(user, id, cancellationToken);
        if (removed is null)
            return;

        // 凭据变了：此前用这个外部账号完成第一步、尚待第二步的登录挑战随之作废
        user.RotateSecurityStamp();
        await userRepository.UpdateAsync(user, cancellationToken);

        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.AuthExternalLoginUnlinked,
            OperationTarget.For(removed.Id, $"{removed.Provider}: {removed.Profile.AccountLabel}"),
            OperationRecordAuthorizations.AuthenticatedSelf,
            cancellationToken);
        await unit.CompleteAsync(cancellationToken);
    }

    private async Task<User> GetCurrentUserEntityAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.Id!.Value;
        return await userRepository.GetByIdAsync(userId, cancellationToken)
            ?? throw new BusinessException(UserErrorCodes.NotFound, $"User {userId} not found.")
                .WithData("Id", userId);
    }

}
#endif
