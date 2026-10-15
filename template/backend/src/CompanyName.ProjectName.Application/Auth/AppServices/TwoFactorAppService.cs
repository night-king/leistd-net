#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.Errors;
using CompanyName.ProjectName.Domain.Users.Errors;
using CompanyName.ProjectName.Application.Auth.Dtos;
using CompanyName.ProjectName.Application.Auth.Events;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Application.Auth.SecurityAlerts;
using CompanyName.ProjectName.Application.Auth.Sessions;
using CompanyName.ProjectName.Domain.Auth.DomainServices;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.DomainServices;
using Leistd.Security.OneTimeCodes;
using Leistd.Ddd.Application.AppServices;
using Leistd.Ddd.Domain.Repositories;
using Leistd.EventBus.Abstractions;
using Leistd.ExceptionHandling;
using Leistd.MultiTenancy.Context;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.Security.Users;
using Leistd.Timing;
using Leistd.Lock.Abstractions;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.Attributes;
using Microsoft.Extensions.Caching.Distributed;

namespace CompanyName.ProjectName.Application.Auth.AppServices;

/// <inheritdoc cref="ITwoFactorAppService" />
/// <remarks>
/// 待启用的密钥放在缓存里而不是用户行上：没确认的设置不该改变账号状态，
/// 半途放弃也不留痕。缓存里存的同样是加密后的密钥。
/// <para>启用与停用各有多次写入（用户行、会话撤销），标 <c>[UnitOfWork]</c> 同生共死；
/// 提醒与设置密钥的清理经本地事件在提交之后执行，回滚的变更不发提醒、也不丢掉还能再确认的设置。</para>
/// </remarks>
internal sealed class TwoFactorAppService(
    IRepository<User, Guid> userRepository,
    ICurrentUser currentUser,
    ICurrentTenant currentTenant,
    TwoFactorDomainService twoFactorDomainService,
    UserDomainService userDomainService,
    UserSessionDomainService userSessionDomainService,
    ILoginSecurityPolicyProvider loginSecurityPolicy,
    IReauthenticationGuard reauthenticationGuard,
    IOperationRecorder operationRecorder,
    ILocalEventBus localEventBus,
    IDistributedCache cache,
    IClock clock,
    IDistributedLock distributedLock,
    IUnitOfWorkManager unitOfWorkManager) : BaseAppService, ITwoFactorAppService
{
    // 身份验证器应用里条目的发行方，生成项目后即项目名
    private const string Issuer = "CompanyName.ProjectName";
    private const string SetupKeyPrefix = "auth:2fa-setup:";
    private static readonly TimeSpan SetupLifetime = TimeSpan.FromMinutes(10);

    /// <inheritdoc />
    public async Task<TwoFactorStatusOutputDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserEntityAsync(cancellationToken);
        var policy = await loginSecurityPolicy.GetAsync(cancellationToken);
        return new TwoFactorStatusOutputDto
        {
            Enabled = user.TwoFactorEnabled,
            RecoveryCodesLeft = user.TwoFactorEnabled ? user.RecoveryCodesLeft : 0,
            RequiredByPolicy = policy.RequireTwoFactor
        };
    }

    /// <inheritdoc />
    public async Task<TwoFactorSetupOutputDto> BeginSetupAsync(CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserEntityAsync(cancellationToken);
        user.EnsureTwoFactorDisabled();

        var secret = Totp.GenerateSecret();
        await cache.SetStringAsync(
            SetupKey(user.Id),
            twoFactorDomainService.ProtectSecret(secret),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = SetupLifetime },
            cancellationToken);

        var base32 = Totp.FormatSecret(secret);
        // 同名账号在不同租户各有一个，账号名带上租户，应用里才分得清
        var account = currentTenant.Name is { Length: > 0 } tenantName
            ? $"{user.Username}@{tenantName}"
            : user.Username;
        return new TwoFactorSetupOutputDto
        {
            Secret = base32,
            OtpAuthUri = Totp.BuildUri(Issuer, account, secret)
        };
    }

    /// <inheritdoc />
    [UnitOfWork]
    public async Task<TwoFactorRecoveryCodesOutputDto> EnableAsync(
        TwoFactorCodeInputDto input,
        CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserEntityAsync(cancellationToken);
        // 先于取缓存与校验验证码拒绝：已启用的账号不该看到"设置已过期"或"验证码不对"
        user.EnsureTwoFactorDisabled();

        var protectedSecret = await cache.GetStringAsync(SetupKey(user.Id), cancellationToken)
            ?? throw new BusinessException(AuthErrorCodes.TwoFactorSetupExpired, "The setup has expired. Start again.");

        if (twoFactorDomainService.VerifySetupCode(protectedSecret, input.Code, clock.Now) is not { } step)
        {
            throw CodeInvalid();
        }

        var codes = RecoveryCodes.Generate();
        user.EnableTwoFactor(protectedSecret, codes.Select(RecoveryCodes.Hash), step);
        await userRepository.UpdateAsync(user, cancellationToken);

        // 登录方式变了，此前的其他会话都是在没有第二道门时建立的
        await userSessionDomainService.RevokeAllAsync(user.Id, currentUser.GetSessionId(), cancellationToken);

        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.AuthTwoFactorEnabled,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            OperationRecordAuthorizations.AuthenticatedSelf,
            cancellationToken);
        await localEventBus.PublishAsync(new TwoFactorSetupCompletedEvent(user.Id, clock.Now), cancellationToken);
        await localEventBus.PublishAsync(
            new SecurityAlertRequestedEvent(user.Id, new SecurityAlert(SecurityAlertKind.TwoFactorEnabled), clock.Now),
            cancellationToken);

        return new TwoFactorRecoveryCodesOutputDto { RecoveryCodes = codes };
    }

    /// <inheritdoc />
    /// <remarks>再认证失败的计数与审计各自独立提交，不随本方法的回滚丢失。</remarks>
    [UnitOfWork]
    public async Task DisableAsync(DisableTwoFactorInputDto input, CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserEntityAsync(cancellationToken);
        if (!user.TwoFactorEnabled)
            return;

        if ((await loginSecurityPolicy.GetAsync(cancellationToken)).RequireTwoFactor)
        {
            throw new BusinessException(AuthErrorCodes.TwoFactorRequiredByPolicy, "Two-factor authentication is required here and cannot be turned off.");
        }

        // 停用两步验证要口令与验证码各过一关，两关都是再认证：锁定期内不给试，失败按登录的同一套计数。
        // 少了这一道，持有被盗会话的人能在这个接口上无限次猜——猜到了就把这个账号的第二道防线拆了。
        await reauthenticationGuard.EnsureAllowedAsync(user, cancellationToken);

        if (!userDomainService.VerifyCurrentPassword(user, input.Password))
        {
            throw await reauthenticationGuard.RejectAsync(
                user,
                OperationRecordActions.AuthTwoFactorDisabled,
                SecurityErrorCodes.CurrentPasswordIncorrect,
                "The current password is incorrect.",
                cancellationToken);
        }

        if (!twoFactorDomainService.VerifyCode(user, input.Code, clock.Now))
        {
            throw await reauthenticationGuard.RejectAsync(
                user,
                OperationRecordActions.AuthTwoFactorDisabled,
                AuthErrorCodes.TwoFactorCodeInvalid,
                CodeInvalidMessage,
                cancellationToken);
        }

        user.DisableTwoFactor();
        await userRepository.UpdateAsync(user, cancellationToken);
        await userSessionDomainService.RevokeAllAsync(user.Id, currentUser.GetSessionId(), cancellationToken);

        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.AuthTwoFactorDisabled,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            OperationRecordAuthorizations.AuthenticatedSelf,
            cancellationToken);
        await localEventBus.PublishAsync(
            new SecurityAlertRequestedEvent(user.Id, new SecurityAlert(SecurityAlertKind.TwoFactorDisabled), clock.Now),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TwoFactorRecoveryCodesOutputDto> RegenerateRecoveryCodesAsync(
        TwoFactorCodeInputDto input,
        CancellationToken cancellationToken = default)
    {
        await using var held = await distributedLock.LockAsync(
            $"MyProject:2fa-login:lock:{currentUser.Id!.Value:N}", cancellationToken);
        using var lockCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockCancellation.Token;
        using var unit = unitOfWorkManager.Begin(requiresNew: true);
        var user = await GetCurrentUserEntityAsync(cancellationToken);
        // 先于再认证拒绝：未启用的账号没有可校验的验证码，不该计入失败次数
        user.EnsureTwoFactorEnabled();

        // 重发恢复码同样是再认证：拿到恢复码等于拿到一组可绕过两步验证的凭据
        await reauthenticationGuard.EnsureAllowedAsync(user, cancellationToken);

        if (!twoFactorDomainService.VerifyCode(user, input.Code, clock.Now))
        {
            throw await reauthenticationGuard.RejectAsync(
                user,
                OperationRecordActions.AuthTwoFactorRecoveryCodesRegenerated,
                AuthErrorCodes.TwoFactorCodeInvalid,
                CodeInvalidMessage,
                cancellationToken);
        }

        var codes = RecoveryCodes.Generate();
        user.ReplaceRecoveryCodes(codes.Select(RecoveryCodes.Hash));
        await userRepository.UpdateAsync(user, cancellationToken);

        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.AuthTwoFactorRecoveryCodesRegenerated,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            OperationRecordAuthorizations.AuthenticatedSelf,
            cancellationToken);

        await unit.CompleteAsync(cancellationToken);
        return new TwoFactorRecoveryCodesOutputDto { RecoveryCodes = codes };
    }

    private const string CodeInvalidMessage = "The verification code is incorrect.";

    private static BusinessException CodeInvalid() =>
        new(AuthErrorCodes.TwoFactorCodeInvalid, CodeInvalidMessage);

    /// <summary>待确认设置密钥的缓存键。</summary>
    internal static string SetupKey(Guid userId) => SetupKeyPrefix + userId.ToString("N");

    private async Task<User> GetCurrentUserEntityAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.Id!.Value;
        return await userRepository.GetByIdAsync(userId, cancellationToken)
            ?? throw new BusinessException(UserErrorCodes.NotFound, $"User {userId} not found.")
                .WithData("Id", userId);
    }
}
#endif
