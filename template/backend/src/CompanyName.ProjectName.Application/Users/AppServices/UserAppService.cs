using Leistd.Data.Querying;
using Leistd.UnitOfWork.Attributes;
using CompanyName.ProjectName.Domain.Users.Errors;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Domain.Users.DomainServices;
#if (RemoteTokenAuth)
using CompanyName.ProjectName.Domain.Users.ValueObjects;
using Leistd.MultiTenancy.Context;
#endif
using Leistd.UnitOfWork;
#if (LocalIdentity)
using CompanyName.ProjectName.Application.Auth.Events;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Application.Auth.Sessions;
using CompanyName.ProjectName.Domain.Auth.DomainServices;
using CompanyName.ProjectName.Application.Auth.SecurityAlerts;
using Leistd.EventBus.Abstractions;
using Leistd.Lock.Abstractions;
#endif
using CompanyName.ProjectName.Application.Permissions.Provider;
using CompanyName.ProjectName.Application.Roles.Dtos;
#if (LocalIdentity)
using CompanyName.ProjectName.Application.Users.Avatars;
using CompanyName.ProjectName.Application.Users.Validators;
#endif
using CompanyName.ProjectName.Application.Users.Dtos;
using CompanyName.ProjectName.Application.Users.Mappings;
using CompanyName.ProjectName.Domain.Users.Policies;
using CompanyName.ProjectName.Application.Shared.Paging;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.Repositories;
using Leistd.Ddd.Application.AppServices;
using Microsoft.Extensions.Logging;

using Leistd.Security.Users;
using Leistd.ExceptionHandling;
using Leistd.Authorization.Checking;
using Leistd.ObjectMapping.Abstractions;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.Data.Paging;
using Leistd.Timing;
#if (OpenIddictServer)
using OpenIddict.Abstractions;
#endif
#if (LocalIdentity && IncludeNotifications)
using Leistd.Notifications.Stores;
#endif
#if (IncludeLocalization)
using Microsoft.Extensions.Localization;
#endif

namespace CompanyName.ProjectName.Application.Users.AppServices;

/// <summary>用户应用服务。</summary>
internal sealed class UserAppService(
    IUserRepository userRepository,
    IRoleRepository roleRepository,
    IPermissionChecker permissionChecker,
    IOperationRecorder operationRecorder,
    UserDomainService userDomainService,
    IUnitOfWorkManager unitOfWorkManager,
#if (RemoteTokenAuth)
    ICurrentTenant currentTenant,
#endif
#if (LocalIdentity)
    UserSortingValidator userSortingValidator,
    UserSessionDomainService userSessionDomainService,
    ILocalEventBus localEventBus,
    IDistributedLock distributedLock,
    IReauthenticationGuard reauthenticationGuard,
#endif
#if (OpenIddictServer)
    IOpenIddictTokenManager tokenManager,
#endif
    ICurrentUser currentUser,
#if (LocalIdentity && IncludeNotifications)
    INotificationStore notificationStore,
#endif
    ILogger<UserAppService> logger,
#if (IncludeLocalization)
    IStringLocalizer localizer,
#endif
    IClock clock,
    IObjectMapper objectMapper,
    IQueryableAsyncExecuter asyncExecuter) : BaseAppService, IUserAppService
{
#if (RemoteTokenAuth)
    /// <summary>OIDC 标准声明：签发方是否已验证该邮箱。</summary>
    private const string EmailVerifiedClaimType = "email_verified";

#endif
    /// <summary>获取用户列表（分页）。</summary>
    public async Task<PagedResult<UserManagementOutputDto>> GetPagedListAsync(
        GetUserPagedInputDto input,
        CancellationToken cancellationToken = default)
    {
        var ordered = (await userRepository.GetQueryableWithRolesAsync(cancellationToken)).OrderBySorting(input.Sorting);
#if (LocalIdentity)
        userSortingValidator.Validate(ordered);
#endif
        IQueryable<User> userQuery = ordered.ThenBy(user => user.Id);

        if (!string.IsNullOrWhiteSpace(input.Keyword))
        {
            var keyword = input.Keyword.Trim();
            userQuery = userQuery.Where(u => u.Username.Contains(keyword) || u.Email.Contains(keyword) || (u.DisplayName != null && u.DisplayName.Contains(keyword)));
        }

        if (input.IsActive.HasValue)
        {
            userQuery = userQuery.Where(u => u.IsActive == input.IsActive.Value);
        }

#if (LocalIdentity)
        if (input.IsEmailVerified.HasValue)
        {
            userQuery = userQuery.Where(u => u.EmailConfirmed == input.IsEmailVerified.Value);
        }
#endif

        if (input.Roles is { Count: > 0 })
        {
            // 规范化：去 null/空白（模型绑定会把空白项转为 null）、去重；单项长度已由入参 DTO 校验。
            var roleNames = input.Roles
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (roleNames.Count > 0)
            {
                var matchedRoles = await roleRepository.GetListAsync(r => roleNames.Contains(r.Name), cancellationToken);
                var roleIds = matchedRoles.Select(r => r.Id).ToList();
                if (roleIds.Count == 0)
                {
                    return new PagedResult<UserManagementOutputDto>(0, []);
                }

                userQuery = userQuery.Where(u => u.Roles.Any(ur => roleIds.Contains(ur.RoleId)));
            }
        }

        var totalCount = await asyncExecuter.CountAsync(userQuery, cancellationToken);
        var users = await asyncExecuter.ToListAsync(
            userQuery.Skip(input.Offset).Take(input.Limit),
            cancellationToken);

        var userDtos = await MapToOutputsAsync(users, cancellationToken);
        return new PagedResult<UserManagementOutputDto>(totalCount, userDtos);
    }


    /// <summary>获取用户详情。</summary>
    public async Task<UserManagementOutputDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await GetUserWithRolesOrThrowAsync(id, cancellationToken);
        return await MapToOutputAsync(user, cancellationToken);
    }
#if (RemoteTokenAuth)

    /// <inheritdoc />
    public async Task<CurrentResourceUserOutputDto> GetCurrentResourceUserAsync(CancellationToken cancellationToken = default)
    {
        var user = currentUser.Id is { } id ? await userRepository.GetWithRolesAsync(id, cancellationToken) : null;
        var local = user is null ? null : await MapToOutputAsync(user, cancellationToken);
        return new CurrentResourceUserOutputDto
        {
            Id = currentUser.Id,
            Username = currentUser.Username ?? "",
            Email = currentUser.Email ?? "",
            DisplayName = currentUser.Name,
            IsEmailVerified = currentUser.FindClaim(EmailVerifiedClaimType)?.Value == "true",
            IsSuperAdmin = local?.IsSuperAdmin ?? false,
            Roles = local?.Roles.Select(role => role.Name).ToArray() ?? [],
            TenantId = currentTenant.Id
        };
    }

    /// <inheritdoc />
    public async Task<UserAccessStatus?> EnsureCurrentUserProjectedAsync(CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.Id is not { } subjectId)
        {
            return null;
        }

        try
        {
            return await ProjectCurrentUserAsync(subjectId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 首次访问的并发：同一个 sub 的两个请求同时插入，输的那个在提交时撞主键。
            // 前端登录后往往并行发好几个请求，第一次访问正好都在投影；重来一次就会读到赢家写下的行。
            // 只重试一次，不做退避循环：第二次还失败就不是竞争，是真有问题，交给调用方
            logger.LogDebug(exception, "Projecting issuer subject {SubjectId} failed once; retrying", subjectId);
            return await ProjectCurrentUserAsync(subjectId, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<UserAccessStatus?> GetCurrentUserAccessStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.Id is not { } subjectId)
        {
            return null;
        }

        var user = await userRepository.GetByIdAsync(subjectId, cancellationToken);
        return user?.GetAccessStatus(clock.Now);
    }

    // 独立工作单元：投影是请求的前置动作，不该被后续业务失败连带回滚——
    // 回滚了下一次请求还要再建一次，而这一行的存在与业务是否成功无关。
    // 状态取自投影读到（或新建）的同一行，提交成功才返回：调用方据此免去再查一次
    private async Task<UserAccessStatus> ProjectCurrentUserAsync(Guid subjectId, CancellationToken cancellationToken)
    {
        using var unitOfWork = unitOfWorkManager.Begin(requiresNew: true);
        var user = await userDomainService.EnsureProjectedAsync(
            subjectId,
            currentUser.Username,
            currentUser.Email,
            currentUser.Name,
            cancellationToken);
        await unitOfWork.CompleteAsync(cancellationToken);
        return user.GetAccessStatus(clock.Now);
    }
#endif

#if (LocalIdentity)
    /// <summary>创建用户。</summary>
    /// <remarks>建用户、补管理字段、分配角色三次写入必须同生共死：中途失败会留下没有任何角色的用户。</remarks>
    [UnitOfWork]
    public async Task<UserManagementOutputDto> CreateAsync(CreateUserInputDto input, CancellationToken cancellationToken = default)
    {
        var username = input.Username.Trim();
        var email = input.Email.Trim();
        var displayName = input.DisplayName?.Trim();

        // 不与用户名同行记邮箱，理由同 AuthAppService：用户名常常就是邮箱本地部，
        // 同一行给出本地部与域名等于把脱敏拼回去
        logger.LogInformation("Creating user {Username}", username);

        // 创建时携带角色等同于一次角色分配，因此除创建权限外还必须持有 ManageRoles，
        // 否则只拥有创建权限的主体可以直接造出一个管理员账号。
        var roles = input.RoleIds.Count > 0
            ? await GetRolesWithManageRolesCheckAsync(input.RoleIds, cancellationToken)
            : await roleRepository.GetDefaultRolesAsync(cancellationToken);
        var user = await userDomainService.CreateUserAsync(
            username, email, input.Password, displayName, cancellationToken: cancellationToken);
        user.SetAvatar(input.Avatar?.Trim());
        if (input.IsEmailVerified)
        {
            user.ConfirmEmail();
        }

        if (!input.IsActive)
        {
            user.Disable();
        }

        user.AssignRoles(roles.Select(role => role.Id));
        await userRepository.UpdateAsync(user, cancellationToken);

        logger.LogInformation("User created (ID: {Id})", user.Id);

        // 跟随本方法的 [UnitOfWork] 边界：建用户回滚，这条记录一并回滚，不留"记了但没发生"的假账
        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.UserCreated,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            PermissionConstant.Users.Create,
            cancellationToken);

        return MapToOutput(user, roles);
    }

    /// <summary>更新用户。</summary>
    /// <remarks>
    /// <para>资源服务形态下没有这个入口：用户名、邮箱、显示名归签发方所有，本地改了没有回写通道，
    /// 只会与签发方漂移。那一侧的资料由 <c>ResourceUserProvisioningMiddleware</c> 每次访问按令牌刷新。</para>
    /// <para>改邮箱或把邮箱标为已验证时要确认是管理员本人（会话刚认证过，或出示自己的当前口令或验证码）：
    /// 已验证的邮箱是按邮箱关联外部登录的依据，被盗的管理员会话据此就能把别人的账号接到自己的外部账号上。
    /// 改的是自己时同样要求；只改显示名、头像不要求。</para>
    /// <para>确认身份在操作者自己的凭据锁内独立提交并释放，之后才取目标用户的凭据锁读、改并提交：两位管理员互相操作时不交叉持锁，
    /// 改的是自己时同一把锁先后取两次。是否敏感按锁内最新的目标行复核：锁前判定不敏感而此刻已敏感时，
    /// 只认会话刚认证过，否则要求客户端带上凭证重试。与本人同时进行的改邮箱、确认邮箱互不覆盖。</para>
    /// </remarks>
    public async Task<UserManagementOutputDto> UpdateAsync(Guid id, UpdateUserInputDto input, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Updating user {Id}", id);

        // 拒绝先于确认身份：不为一个注定被拒的请求消耗验证码
        var target = await GetUserOrThrowAsync(id, cancellationToken);
        EnsureUpdatable(target);
        var confirmed = false;
        if (IsSensitiveManagementChange(target, input))
        {
            await reauthenticationGuard.EnsureRecentInOwnLockAsync(
                new ReauthenticationProof(input.Password, input.TwoFactorCode),
                OperationRecordActions.UserUpdated,
                cancellationToken);
            confirmed = true;
        }

        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(id), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;
        using var unit = unitOfWorkManager.Begin(requiresNew: true);

        var user = await GetUserWithRolesOrThrowAsync(id, cancellationToken);
        EnsureUpdatable(user);
        if (!confirmed && IsSensitiveManagementChange(user, input))
        {
            // 不出示凭证的判定不消耗任何东西，不需要操作者的锁；改的是自己时也就不会嵌套取同一把锁
            await reauthenticationGuard.EnsureRecentAsync(
                ReauthenticationProof.None, OperationRecordActions.UserUpdated, cancellationToken);
        }

        // 编辑表单会把读到的头像地址原样送回，那表示"没改"，换回存储的原值再校验。
        await userDomainService.UpdateManagementAsync(
            user,
            input.Email.Trim(),
            input.DisplayName?.Trim(),
            AvatarUrls.ResolveSubmitted(user, input.Avatar),
            input.IsEmailVerified,
            cancellationToken);
        // 角色不在此处变更：普通资料更新与角色分配是两个命令、两个权限。
        await userRepository.UpdateAsync(user, cancellationToken);

        logger.LogInformation("User updated (ID: {Id})", user.Id);

        // 补齐成功路径：此前只有 UserController 上的 [OperationRecordAction] 记被拒的更新，
        // 成功反而不留痕——而注解自己的文档写着"与成功路径使用的值逐字一致"，它预设了这里存在。
        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.UserUpdated,
            OperationTarget.For(id, user.DisplayName ?? user.Username),
            PermissionConstant.Users.Update,
            cancellationToken);
        await unit.CompleteAsync(cancellationToken);

        return await MapToOutputAsync(user, cancellationToken);
    }

    private void EnsureUpdatable(User user)
    {
        if (!user.CanBeManagedBy(currentUser.Id))
        {
            throw new BusinessException(UserErrorCodes.SuperAdminUpdateForbidden, "The built-in super administrator cannot be updated by other administrators.");
        }
    }

    // 与领域判定"换了邮箱"同一口径（大小写不敏感）；标为已验证也算：已验证的邮箱是按邮箱关联外部登录的依据
    private static bool IsSensitiveManagementChange(User user, UpdateUserInputDto input) =>
        !string.Equals(user.Email, input.Email.Trim(), StringComparison.OrdinalIgnoreCase) ||
        (input.IsEmailVerified && !user.EmailConfirmed);

#endif
    /// <summary>启用用户。</summary>
    /// <remarks>两种形态都保留：即使身份由签发方发放，本服务仍要能就地停掉一个人的访问。</remarks>
    public async Task EnableAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnableCoreAsync(await GetUserOrThrowAsync(id, cancellationToken), cancellationToken);
    }

    // 规则与留痕只写在这里，单个入口与批量入口共用
    private async Task EnableCoreAsync(User user, CancellationToken cancellationToken)
    {
        if (!user.CanBeManagedBy(currentUser.Id))
        {
            throw new BusinessException(UserErrorCodes.SuperAdminOperationForbidden, "The built-in super administrator cannot be operated on by other administrators.");
        }

        // 已启用时静默成功、不留记录（同解锁）：重复点击不该留下一串没发生过的事
        if (user.IsActive)
        {
            return;
        }

        user.Enable();
        await userRepository.UpdateAsync(user, cancellationToken);

        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.UserEnabled,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            PermissionConstant.Users.Update,
            cancellationToken);
    }

    /// <summary>禁用用户。</summary>
#if (LocalIdentity)
    /// <remarks>停用与撤销同生共死；撤销的时序见 <see cref="RevokeAllAccessAsync"/>。</remarks>
    [UnitOfWork]
#endif
    public async Task DisableAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await DisableCoreAsync(await GetUserOrThrowAsync(id, cancellationToken), cancellationToken);
    }

    // 规则与留痕只写在这里，单个入口与批量入口共用；工作单元由入口给出
    private async Task DisableCoreAsync(User user, CancellationToken cancellationToken)
    {
        if (!user.CanBeManagedBy(currentUser.Id))
        {
            throw new BusinessException(UserErrorCodes.SuperAdminDisableForbidden, "The built-in super administrator cannot be disabled by other administrators.");
        }
        if (!user.CanBeDisabled())
        {
            throw new BusinessException(UserErrorCodes.SuperAdminDisableSelfForbidden, "The built-in super administrator cannot disable itself.");
        }
        // 拒绝必须在任何写入之前：后面的撤销会把操作者自己的会话一起作废
        if (!user.CanBeDisabledBy(currentUser.Id))
        {
            throw new BusinessException(UserErrorCodes.SelfDisableForbidden, "You cannot disable your own account.");
        }

        var wasActive = user.IsActive;
        user.Disable();
        await userRepository.UpdateAsync(user, cancellationToken);

        // 记录只在状态真正变化时写；下面的撤销照做，兜住此前遗留的会话与令牌
        if (wasActive)
        {
            await operationRecorder.RecordSucceededAsync(
                OperationRecordActions.UserDisabled,
                OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
                PermissionConstant.Users.Update,
                cancellationToken);
        }
#if (LocalIdentity)

        // 已在线的会话与已签发的令牌随之作废：登录时的启用检查挡不住它们
        await RevokeAllAccessAsync(user.Id, keepSessionId: null, cancellationToken);
#endif
    }

#if (LocalIdentity)
    /// <summary>重置用户密码，并撤销该用户的全部会话。</summary>
    /// <remarks>
    /// <para>要确认是管理员本人（会话刚认证过，或出示自己的当前口令或验证码）：被盗的管理员会话不能直接把别人的口令换成自己知道的。
    /// 重置的是自己时同样要求，这条路径保留（本人经用户管理重置口令并保留当前会话）。</para>
    /// <para>确认身份在操作者自己的凭据锁内独立提交并释放，之后才取目标用户的凭据锁读、改并提交：同时进行的口令登录要么先登记完会话、
    /// 被这里一并撤掉，要么等这里提交后再按新口令判定。口令与撤销同生共死；提醒在提交之后发出。</para>
    /// </remarks>
    public async Task ResetPasswordAsync(Guid id, ResetUserPasswordInputDto input, CancellationToken cancellationToken = default)
    {
        await reauthenticationGuard.EnsureRecentInOwnLockAsync(
            new ReauthenticationProof(input.CurrentPassword, input.TwoFactorCode),
            OperationRecordActions.UserPasswordReset,
            cancellationToken);

        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(id), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;
        using var unit = unitOfWorkManager.Begin(requiresNew: true);

        var user = await GetUserOrThrowAsync(id, cancellationToken);
        if (!user.CanBeManagedBy(currentUser.Id))
        {
            throw new BusinessException(UserErrorCodes.SuperAdminResetPasswordForbidden, "The built-in super administrator's password cannot be reset by other administrators.");
        }

        userDomainService.ResetPassword(user, input.Password);
        await userRepository.UpdateAsync(user, cancellationToken);

        await localEventBus.PublishAsync(
            new SecurityAlertRequestedEvent(user.Id, new SecurityAlert(SecurityAlertKind.PasswordReset), clock.Now),
            cancellationToken);
        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.UserPasswordReset,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            PermissionConstant.Users.Update,
            cancellationToken);

        // 密码被管理员重置，意味着账号可能已不在本人掌控之下：此前的会话全部作废。
        // 重置的是自己时保留当前这台，免得操作完把自己踢出去
        await RevokeAllAccessAsync(
            user.Id,
            user.Id == currentUser.Id ? currentUser.GetSessionId() : null,
            cancellationToken);
        await unit.CompleteAsync(cancellationToken);
    }

    /// <summary>解除用户的登录锁定。</summary>
    /// <remarks>
    /// 未锁定时静默成功、不留记录：解锁是幂等的，重复点击不该在操作记录里留下一串没发生过的事。
    /// </remarks>
    public async Task UnlockAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await UnlockCoreAsync(await GetUserOrThrowAsync(id, cancellationToken), cancellationToken);
    }

    // 规则与留痕只写在这里，单个入口与批量入口共用
    private async Task UnlockCoreAsync(User user, CancellationToken cancellationToken)
    {
        if (!user.Lockout.HasAnythingToClear)
        {
            return;
        }

        user.Unlock();
        await userRepository.UpdateAsync(user, cancellationToken);

        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.UserUnlocked,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            PermissionConstant.Users.Update,
            cancellationToken);
    }

    /// <summary>重置用户的两步验证：停用并清掉密钥与恢复码，该用户的会话全部失效。</summary>
    /// <remarks>
    /// <para>给丢了手机又没了恢复码的人用。会话一并作废：能走到这一步，说明账号的第二道门已经不在本人手里。
    /// 所在租户要求两步验证时，本人下次登录会被带去重新设置。</para>
    /// <para>不能重置自己（<see cref="User.CanTwoFactorBeResetBy"/>）：本人走个人安全设置的换绑或停用。
    /// 要确认是管理员本人（会话刚认证过，或出示自己的当前口令或验证码）——被盗的管理员会话不能直接拆掉别人的第二道门，
    /// 单独重置口令对启用了两步验证的账号仍然登不进去，两步一起做才能接管。</para>
    /// <para>拒绝（内置超管、重置自己）先于确认身份：不为一个注定被拒的请求消耗验证码。确认身份在操作者自己的凭据锁内独立提交并释放，
    /// 之后才取目标用户的凭据锁读、改并提交，与本人同时进行的换绑、重发、登录第二步互不覆盖；提醒在提交之后发出。</para>
    /// </remarks>
    public async Task ResetTwoFactorAsync(Guid id, ResetUserTwoFactorInputDto input, CancellationToken cancellationToken = default)
    {
        var actorId = currentUser.Id!.Value;
        EnsureTwoFactorResettable(await GetUserOrThrowAsync(id, cancellationToken), actorId);
        await reauthenticationGuard.EnsureRecentInOwnLockAsync(
            new ReauthenticationProof(input.Password, input.TwoFactorCode),
            OperationRecordActions.UserTwoFactorReset,
            cancellationToken);

        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(id), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;
        using var unit = unitOfWorkManager.Begin(requiresNew: true);

        var user = await GetUserOrThrowAsync(id, cancellationToken);
        EnsureTwoFactorResettable(user, actorId);
        if (!user.TwoFactorEnabled)
        {
            return;
        }

        user.DisableTwoFactor();
        await userRepository.UpdateAsync(user, cancellationToken);

        await localEventBus.PublishAsync(
            new SecurityAlertRequestedEvent(user.Id, new SecurityAlert(SecurityAlertKind.TwoFactorReset), clock.Now),
            cancellationToken);
        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.UserTwoFactorReset,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            PermissionConstant.Users.Update,
            cancellationToken);

        await RevokeAllAccessAsync(user.Id, keepSessionId: null, cancellationToken);
        await unit.CompleteAsync(cancellationToken);
    }

    private static void EnsureTwoFactorResettable(User user, Guid actorId)
    {
        if (!user.CanBeManagedBy(actorId))
        {
            throw new BusinessException(UserErrorCodes.SuperAdminOperationForbidden, "The built-in super administrator cannot be operated on by other administrators.");
        }

        if (!user.CanTwoFactorBeResetBy(actorId))
        {
            throw new BusinessException(UserErrorCodes.SelfTwoFactorResetForbidden, "You can't reset your own two-factor authentication here. Use your security settings.");
        }
    }
#endif

#if (LocalIdentity)
    /// <summary>删除用户（软删除）。</summary>
    /// <remarks>
    /// 软删除保留该用户的直授权限与授权版本：软删除是可恢复的，恢复后权限跟着一起回来才合理；
    /// 删了权限再恢复，得到的是一个"存在但什么都不能做"的账号，没人会预期这个结果。
    /// 主体被永久删除时才调用 <c>IPermissionGrantManager.RemoveProviderAsync</c> 清理，
    /// 例如角色删除（<c>RoleAppService.DeleteAsync</c>）。
    /// <para>站内通知一并删掉：它们只对本人有意义，留下来就是没人能读、也没人能删的孤儿行。</para>
    /// <para>删除、清理与撤销同生共死；撤销的时序见 <see cref="RevokeAllAccessAsync"/>。</para>
    /// <para>删除幂等：用户不存在（含已删除）时直接成功，不写操作记录——什么也没删。</para>
    /// </remarks>
    [UnitOfWork]
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.GetByIdAsync(id, cancellationToken);
        if (user is null)
            return;

        logger.LogInformation("Deleting user {Id}", id);
        if (!user.CanBeDeleted())
        {
            throw new BusinessException(UserErrorCodes.SuperAdminDeleteForbidden, "The built-in super administrator cannot be deleted.");
        }
        if (!user.CanBeDeletedBy(currentUser.Id))
        {
            throw new BusinessException(UserErrorCodes.SelfDeleteForbidden, "You cannot delete your own account.");
        }

        await userRepository.DeleteAsync(user, cancellationToken);
#if (IncludeNotifications)
        await notificationStore.DeleteAllAsync(id.ToString(), cancellationToken);
#endif
        logger.LogInformation("User deleted (ID: {Id})", id);

        // 名字在删除前就握在手里（user 变量即是）：删完再查什么都查不到，
        // 而审计要回答的正是"当时删掉的是谁"。
        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.UserDeleted,
            OperationTarget.For(id, user.DisplayName ?? user.Username),
            PermissionConstant.Users.Delete,
            cancellationToken);

        await RevokeAllAccessAsync(user.Id, keepSessionId: null, cancellationToken);
    }

#endif
    private async Task<User> GetUserOrThrowAsync(Guid id, CancellationToken cancellationToken)
        => await userRepository.GetByIdAsync(id, cancellationToken) ?? throw UserNotFound(id);

    private async Task<User> GetUserWithRolesOrThrowAsync(Guid id, CancellationToken cancellationToken)
        => await userRepository.GetWithRolesAsync(id, cancellationToken) ?? throw UserNotFound(id);

    private static BusinessException UserNotFound(Guid id)
        => new BusinessException(UserErrorCodes.NotFound, $"User {id} not found.").WithData("Id", id);

    /// <inheritdoc />
    public async Task<UserAvatarOutputDto?> GetAvatarAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.GetByIdAsync(id, cancellationToken);
        return user is not null && AvatarPolicy.TryReadImage(user.Avatar, out var image)
            ? new UserAvatarOutputDto(image.ContentType, image.Bytes)
            : null;
    }

    /// <summary>查询用户当前角色。</summary>
    public async Task<IReadOnlyList<RoleBriefOutputDto>> GetRolesAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var roleIds = (await GetUserWithRolesOrThrowAsync(id, cancellationToken)).GetRoleIds();
        if (roleIds.Count == 0)
        {
            return [];
        }

        var roles = (await roleRepository.GetListAsync(r => roleIds.Contains(r.Id), cancellationToken)).ToList();
        return ToRoleBriefs(roles);
    }

    /// <summary>替换用户角色。调用方必须持有 App.Users.ManageRoles，由 Controller 上的策略保证。</summary>
    /// <remarks>撤销与新增在同一工作单元提交：拆成两次提交时，后一步失败会把用户留在零角色状态。</remarks>
    [UnitOfWork]
    public async Task<IReadOnlyList<RoleBriefOutputDto>> ReplaceRolesAsync(
        Guid id,
        UpdateUserRolesInputDto input,
        CancellationToken cancellationToken = default)
    {
        var user = await GetUserWithRolesOrThrowAsync(id, cancellationToken);
        EnsureRolesCanBeManaged(user);

        var roles = await GetRolesByIdsAsync(input.RoleIds, cancellationToken);
        user.ReplaceRoles(roles.Select(role => role.Id));
        await userRepository.UpdateAsync(user, cancellationToken);

        logger.LogInformation("User roles replaced (ID: {Id}, role count: {Count})", id, roles.Count);

        // 本方法标了 [UnitOfWork]：这条记录跟随该工作单元，角色替换回滚则记录一并回滚——
        // 成功记录必须与它描述的那次变更同生共死。与下面的 UpdateAsync 不同，那里没有工作单元。
        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.UserRolesReplaced,
            OperationTarget.For(id, user.DisplayName ?? user.Username),
            PermissionConstant.Users.ManageRoles,
            cancellationToken);

        return ToRoleBriefs(roles);
    }

    /// <inheritdoc />
    public Task<BatchOperationOutputDto> BatchEnableAsync(
        BatchUsersInputDto input,
        CancellationToken cancellationToken = default)
        => ExecuteBatchAsync(
            input.Ids,
            OperationRecordActions.UserEnabled,
            PermissionConstant.Users.Update,
            GetUserOrThrowAsync,
            EnableCoreAsync,
            cancellationToken);

    /// <inheritdoc />
    public Task<BatchOperationOutputDto> BatchDisableAsync(
        BatchUsersInputDto input,
        CancellationToken cancellationToken = default)
        => ExecuteBatchAsync(
            input.Ids,
            OperationRecordActions.UserDisabled,
            PermissionConstant.Users.Update,
            GetUserOrThrowAsync,
            DisableCoreAsync,
            cancellationToken);
#if (LocalIdentity)

    /// <inheritdoc />
    public Task<BatchOperationOutputDto> BatchUnlockAsync(
        BatchUsersInputDto input,
        CancellationToken cancellationToken = default)
        => ExecuteBatchAsync(
            input.Ids,
            OperationRecordActions.UserUnlocked,
            PermissionConstant.Users.Update,
            GetUserOrThrowAsync,
            UnlockCoreAsync,
            cancellationToken);
#endif

    /// <inheritdoc />
    /// <remarks>
    /// 角色在逐条执行之前解析一次：有不存在的角色时整个请求被拒，一条都不执行。
    /// 记录沿用角色替换的动作码，它回答的是同一个问题——这个人的角色变了。
    /// </remarks>
    public async Task<BatchOperationOutputDto> BatchAssignRolesAsync(
        BatchAssignUserRolesInputDto input,
        CancellationToken cancellationToken = default)
    {
        var roleIds = (await GetRolesByIdsAsync(input.RoleIds, cancellationToken)).Select(role => role.Id).ToList();
        return await ExecuteBatchAsync(
            input.Ids,
            OperationRecordActions.UserRolesReplaced,
            PermissionConstant.Users.ManageRoles,
            GetUserWithRolesOrThrowAsync,
            (user, ct) => AssignRolesCoreAsync(user, roleIds, ct),
            cancellationToken);
    }

    /// <remarks>用户须已带角色读取。</remarks>
    private async Task AssignRolesCoreAsync(User user, List<Guid> roleIds, CancellationToken cancellationToken)
    {
        EnsureRolesCanBeManaged(user);

        // 已持有全部角色时静默成功、不留记录（同启用与解锁）
        if (roleIds.All(user.IsInRole))
        {
            return;
        }

        user.AssignRoles(roleIds);
        await userRepository.UpdateAsync(user, cancellationToken);
        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.UserRolesReplaced,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            PermissionConstant.Users.ManageRoles,
            cancellationToken);
    }

    // 角色替换与追加共用的前置拒绝
    private void EnsureRolesCanBeManaged(User user)
    {
        if (!user.CanBeManagedBy(currentUser.Id))
        {
            throw new BusinessException(UserErrorCodes.SuperAdminUpdateForbidden, "The built-in super administrator cannot be updated by other administrators.");
        }
    }

    /// <summary>逐条执行批量操作：每条在自己的工作单元里提交，业务拒绝只回滚这一条。</summary>
    /// <remarks>
    /// <para>被拒的条目由这里补一条失败记录：单个入口靠端点注解兜底补记，批量请求里一次有多个目标，兜底推不出是哪一个。</para>
    /// <para>只接住业务拒绝；技术异常照常抛出，此前已提交的条目保持提交。</para>
    /// </remarks>
    private async Task<BatchOperationOutputDto> ExecuteBatchAsync(
        IEnumerable<Guid> ids,
        string action,
        string authorizationBasis,
        Func<Guid, CancellationToken, Task<User>> load,
        Func<User, CancellationToken, Task> execute,
        CancellationToken cancellationToken)
    {
        var items = new List<BatchOperationItemOutputDto>();
        foreach (var id in ids.Distinct())
        {
            var target = OperationTarget.For(id);
            try
            {
                using var unitOfWork = unitOfWorkManager.Begin(requiresNew: true);
                var user = await load(id, cancellationToken);
                target = OperationTarget.For(user.Id, user.DisplayName ?? user.Username);
                await execute(user, cancellationToken);
                await unitOfWork.CompleteAsync(cancellationToken);
                items.Add(new BatchOperationItemOutputDto { Id = id, Succeeded = true });
            }
            catch (BusinessException exception)
            {
                await operationRecorder.RecordFailedAsync(
                    action,
                    target,
                    authorizationBasis,
                    OperationFailure.FromCode(exception.Code, exception.LocalizationData));
                items.Add(new BatchOperationItemOutputDto
                {
                    Id = id,
                    Succeeded = false,
                    ErrorCode = exception.Code,
                    Message = Localize(exception)
                });
            }
        }

        return new BatchOperationOutputDto { Items = items };
    }

    /// <summary>与错误响应的 <c>detail</c> 同一口径：按错误码取词条填占位符，取不到时用异常自带的安全文案。</summary>
    private string Localize(BusinessException exception)
    {
#if (IncludeLocalization)
        var localized = localizer[exception.Code];
        if (!localized.ResourceNotFound)
        {
            return LocalizationPlaceholders.Fill(localized.Value, exception.LocalizationData);
        }

#endif
        return exception.Message;
    }

    /// <summary>解析角色前先确认调用方持有角色分配权限。</summary>
    private async Task<List<Role>> GetRolesWithManageRolesCheckAsync(
        List<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        if (!await permissionChecker.IsGrantedAsync(PermissionConstant.Users.ManageRoles, cancellationToken))
        {
            throw new BusinessException(UserErrorCodes.ManageRolesRequired, "Assigning roles requires the user role management permission.");
        }

        return await GetRolesByIdsAsync(roleIds, cancellationToken);
    }

    /// <summary>按 Id 解析角色。角色名只用于展示与筛选，写入路径一律按 Id，避免大小写与重名歧义。</summary>
    private async Task<List<Role>> GetRolesByIdsAsync(List<Guid> roleIds, CancellationToken cancellationToken)
    {
        var normalized = roleIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (normalized.Count == 0)
        {
            return [];
        }

        var roles = (await roleRepository.GetListAsync(r => normalized.Contains(r.Id), cancellationToken)).ToList();
        var missing = normalized.Except(roles.Select(r => r.Id)).ToList();
        if (missing.Count != 0)
        {
            throw new BusinessException(UserErrorCodes.RolesNotFound, $"Roles not found: {string.Join(", ", missing)}")
                .WithData("Roles", string.Join(", ", missing));
        }

        return roles;
    }

    private async Task<List<UserManagementOutputDto>> MapToOutputsAsync(List<User> users, CancellationToken cancellationToken)
    {
        if (users.Count == 0)
        {
            return [];
        }

        var roles = await GetRolesOfAsync(users, cancellationToken);
        return objectMapper.Map<List<User>, List<UserManagementOutputDto>>(users, CreateMappingContext(roles));
    }

    /// <remarks>用户须已带角色读取。</remarks>
    private async Task<UserManagementOutputDto> MapToOutputAsync(User user, CancellationToken cancellationToken)
    {
        var roles = await GetRolesOfAsync([user], cancellationToken);
        return objectMapper.Map<User, UserManagementOutputDto>(user, CreateMappingContext(roles));
    }

    private async Task<List<Role>> GetRolesOfAsync(List<User> users, CancellationToken cancellationToken)
    {
        var roleIds = users.SelectMany(u => u.GetRoleIds()).Distinct().ToList();
        return roleIds.Count == 0 ? [] : (await roleRepository.GetListAsync(r => roleIds.Contains(r.Id), cancellationToken)).ToList();
    }

    /// <remarks>
    /// 供刚写入的路径使用：角色实体由调用方给出，不回查数据库。
    /// 写方法本来就知道自己分配了哪些角色，回查既多一次往返，又要求成员关系已经落库。
    /// </remarks>
    private UserManagementOutputDto MapToOutput(User user, List<Role> roles)
    {
        return objectMapper.Map<User, UserManagementOutputDto>(user, CreateMappingContext(roles));
    }

    /// <remarks>排序是展示口径，映射交给已注册的 <c>Role → RoleBriefOutputDto</c>，不在此手工构造 DTO。</remarks>
    private List<RoleBriefOutputDto> ToRoleBriefs(List<Role> roles)
    {
        var ordered = roles
            .OrderBy(r => r.Sort)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
        return objectMapper.Map<List<Role>, List<RoleBriefOutputDto>>(ordered);
    }

    private Dictionary<string, object> CreateMappingContext(List<Role> roles)
    {
        return new Dictionary<string, object>
        {
            [UserMappings.RolesKey] = roles,
#if (LocalIdentity)
            [UserMappings.NowKey] = clock.Now,
#endif
        };
    }

#if (LocalIdentity)
    /// <summary>作废该用户已建立的会话与已签发的令牌。调用方在工作单元内、把它放在最后一步。</summary>
    /// <remarks>
    /// <para>撤权要对已签发的凭据生效：会话 Cookie 由会话校验按登记的会话拒绝，Bearer 令牌由令牌记录校验按撤销状态拒绝，
    /// 两者都在认证阶段就失效。</para>
    /// <para>会话删除与调用方的写入同在一个事务里提交。</para>
#if (OpenIddictServer)
    /// <para>令牌撤销不在这个事务里：OpenIddict 存储用自己的上下文与连接（多租户时用户还在另一个库），
    /// 撤销语句当场生效。所以它放在最后、提交之前：撤不掉就抛出，整个用例回滚，不留"账号停了、令牌还能用"的状态；
    /// 撤销之后只剩提交本身可能失败，那时令牌已作废而用例回滚，结果是本人需要重新登录，错在安全一侧。
    /// 不放到提交后处理器：那里失败时变更已提交、令牌仍然有效，而重置两步验证与删除的重试会在前置检查处返回，不再撤销。</para>
#endif
    /// </remarks>
    /// <param name="userId">用户 Id。</param>
    /// <param name="keepSessionId">保留的会话（管理员操作的是自己时保留当前这台），没有则为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task RevokeAllAccessAsync(Guid userId, Guid? keepSessionId, CancellationToken cancellationToken)
    {
        await userSessionDomainService.RevokeAllAsync(userId, keepSessionId, cancellationToken);
#if (OpenIddictServer)
        // 官方批量撤销：EF 存储里是一条 ExecuteUpdate，不必先把该主体的令牌全部读进内存
        await tokenManager.RevokeBySubjectAsync(userId.ToString(), cancellationToken);
#endif
    }
#endif
}
