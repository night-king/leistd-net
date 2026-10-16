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
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace CompanyName.ProjectName.Application.Auth.AppServices;

/// <inheritdoc cref="ITwoFactorAppService" />
/// <remarks>
/// <para>待确认的设置放在缓存里而不是用户行上：没确认的设置不该改变账号状态，半途放弃也不留痕。
/// 缓存里存的同样是加密后的密钥，并带着设置 Id 与开始时的安全版本：别处重新开始过（Id 对不上）、
/// 之后凭据变过（安全版本对不上），这一次设置都作废。</para>
/// <para>改写凭据的方法都先取 <see cref="UserCredentialLock"/>，再读用户、改写，并在锁内用手动工作单元提交；
/// 提醒与设置密钥的清理经本地事件在提交之后执行，回滚的变更不发提醒、也不丢掉还能再确认的设置。
/// 再认证失败的计数与审计各自独立提交，不随本类方法的回滚丢失。</para>
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
    /// <remarks>
    /// <para>先确认会话的主人刚认证过：拿到会话就能把自己的验证器绑上去的话，被盗会话能把本人挡在第二步之外。</para>
    /// <para>换绑再出示一次第二因素：现有验证器上的码，或者验证器丢了时用一个恢复码。恢复码在这里当场用掉，
    /// 只换得一次重新绑定的机会，新的一组恢复码要等新验证器确认后才发——恢复码不能直接换出新的恢复码。</para>
    /// </remarks>
    public async Task<TwoFactorSetupOutputDto> BeginSetupAsync(
        BeginTwoFactorSetupInputDto input,
        CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id!.Value;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;
        using var unit = unitOfWorkManager.Begin(requiresNew: true);

        var user = await GetCurrentUserEntityAsync(cancellationToken);
        var replacing = user.TwoFactorEnabled;
        // 已启用又没出示任何第二因素：是"再启用一次"而不是换绑，不当作一次猜错计数
        if (replacing && string.IsNullOrWhiteSpace(input.Code) && string.IsNullOrWhiteSpace(input.RecoveryCode))
        {
            user.EnsureTwoFactorDisabled();
        }

        var action = replacing
            ? OperationRecordActions.AuthTwoFactorAuthenticatorReplaced
            : OperationRecordActions.AuthTwoFactorEnabled;
        await reauthenticationGuard.EnsureRecentlyAuthenticatedAsync(user, input.Password, action, cancellationToken);

        if (replacing)
        {
            await ProveCurrentSecondFactorAsync(user, input, action, cancellationToken);
        }

        var secret = Totp.GenerateSecret();
        var setup = new PendingSetup(
            Guid.NewGuid(),
            twoFactorDomainService.ProtectSecret(secret),
            user.SecurityStamp,
            replacing);

        await unit.CompleteAsync(cancellationToken);
        // 提交之后再写：换绑用掉的恢复码没落库时，不该留下一份可以确认的设置
        await cache.SetStringAsync(
            SetupKey(user.Id),
            JsonSerializer.Serialize(setup),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = SetupLifetime },
            cancellationToken);

        var base32 = Totp.FormatSecret(secret);
        // 同名账号在不同租户各有一个，账号名带上租户，应用里才分得清
        var account = currentTenant.Name is { Length: > 0 } tenantName
            ? $"{user.Username}@{tenantName}"
            : user.Username;
        return new TwoFactorSetupOutputDto
        {
            SetupId = setup.Id,
            Secret = base32,
            OtpAuthUri = Totp.BuildUri(Issuer, account, secret)
        };
    }

    /// <inheritdoc />
    public async Task<TwoFactorRecoveryCodesOutputDto> EnableAsync(
        EnableTwoFactorInputDto input,
        CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id!.Value;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;
        using var unit = unitOfWorkManager.Begin(requiresNew: true);

        var user = await GetCurrentUserEntityAsync(cancellationToken);
        var setup = await GetPendingSetupAsync(user.Id, cancellationToken);
        // 先于"过期"拒绝：首次设置时账号已启用，说明在别处启用过，告诉本人"已经启用"而不是让他重来
        if (setup is { Replacing: false })
        {
            user.EnsureTwoFactorDisabled();
        }

        if (setup is null || setup.Id != input.SetupId!.Value || !user.HasSecurityStamp(setup.SecurityStamp)
            || setup.Replacing != user.TwoFactorEnabled)
        {
            throw new BusinessException(AuthErrorCodes.TwoFactorSetupExpired, "The setup is no longer valid. Start again.");
        }

        if (twoFactorDomainService.VerifySetupCode(setup.ProtectedSecret, input.Code, clock.Now) is not { } step)
        {
            throw CodeInvalid();
        }

        var codes = RecoveryCodes.Generate();
        if (setup.Replacing)
        {
            user.ReplaceTwoFactor(setup.ProtectedSecret, codes.Select(RecoveryCodes.Hash), step);
        }
        else
        {
            user.EnableTwoFactor(setup.ProtectedSecret, codes.Select(RecoveryCodes.Hash), step);
        }

        await userRepository.UpdateAsync(user, cancellationToken);

        // 登录方式变了：此前的其他会话是在旧的（或没有）第二道门时建立的
        await userSessionDomainService.RevokeAllAsync(user.Id, currentUser.GetSessionId(), cancellationToken);

        await operationRecorder.RecordSucceededAsync(
            setup.Replacing
                ? OperationRecordActions.AuthTwoFactorAuthenticatorReplaced
                : OperationRecordActions.AuthTwoFactorEnabled,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            OperationRecordAuthorizations.AuthenticatedSelf,
            cancellationToken);
        await localEventBus.PublishAsync(new TwoFactorSetupCompletedEvent(user.Id, clock.Now), cancellationToken);
        await localEventBus.PublishAsync(
            new SecurityAlertRequestedEvent(
                user.Id,
                new SecurityAlert(setup.Replacing ? SecurityAlertKind.TwoFactorAuthenticatorReplaced : SecurityAlertKind.TwoFactorEnabled),
                clock.Now),
            cancellationToken);

        await unit.CompleteAsync(cancellationToken);
        return new TwoFactorRecoveryCodesOutputDto { RecoveryCodes = codes };
    }

    /// <inheritdoc />
    public async Task DisableAsync(DisableTwoFactorInputDto input, CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id!.Value;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;
        using var unit = unitOfWorkManager.Begin(requiresNew: true);

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

        await unit.CompleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TwoFactorRecoveryCodesOutputDto> RegenerateRecoveryCodesAsync(
        TwoFactorCodeInputDto input,
        CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id!.Value;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;
        using var unit = unitOfWorkManager.Begin(requiresNew: true);

        var user = await GetCurrentUserEntityAsync(cancellationToken);
        // 先于再认证拒绝：未启用的账号没有可校验的验证码，不该计入失败次数
        user.EnsureTwoFactorEnabled();

        // 重发恢复码同样是再认证：拿到恢复码等于拿到一组可绕过两步验证的凭据。
        // 只认验证码，不认恢复码：恢复码不能用来换出一组新的恢复码
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

    /// <summary>换绑前再出示一次现有的第二因素；失败按再认证计数。用掉的步或恢复码随本次工作单元落库。</summary>
    private async Task ProveCurrentSecondFactorAsync(
        User user,
        BeginTwoFactorSetupInputDto input,
        string action,
        CancellationToken cancellationToken)
    {
        var usedRecoveryCode = !string.IsNullOrWhiteSpace(input.RecoveryCode);
        var proven = usedRecoveryCode
            ? twoFactorDomainService.UseRecoveryCode(user, input.RecoveryCode!)
            : !string.IsNullOrWhiteSpace(input.Code) && twoFactorDomainService.VerifyCode(user, input.Code, clock.Now);
        if (!proven)
        {
            throw await reauthenticationGuard.RejectAsync(
                user,
                action,
                AuthErrorCodes.TwoFactorCodeInvalid,
                CodeInvalidMessage,
                cancellationToken);
        }

        await userRepository.UpdateAsync(user, cancellationToken);
        if (usedRecoveryCode)
        {
            await operationRecorder.RecordSucceededAsync(
                OperationRecordActions.AuthTwoFactorRecoveryCodeUsed,
                OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
                OperationRecordAuthorizations.AuthenticatedSelf,
                cancellationToken);
        }
    }

    private async Task<PendingSetup?> GetPendingSetupAsync(Guid userId, CancellationToken cancellationToken)
    {
        var json = await cache.GetStringAsync(SetupKey(userId), cancellationToken);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PendingSetup>(json);
        }
        catch (JsonException)
        {
            // 读不懂的条目（例如升级前写下的旧格式）按已失效处理，本人重新开始即可
            return null;
        }
    }

    private async Task<User> GetCurrentUserEntityAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.Id!.Value;
        return await userRepository.GetByIdAsync(userId, cancellationToken)
            ?? throw new BusinessException(UserErrorCodes.NotFound, $"User {userId} not found.")
                .WithData("Id", userId);
    }

    /// <summary>一次待确认的设置。</summary>
    /// <param name="Id">设置 Id，确认时要对得上。</param>
    /// <param name="ProtectedSecret">加密后的新密钥。</param>
    /// <param name="SecurityStamp">开始设置时用户的安全版本。</param>
    /// <param name="Replacing">是否为换绑（开始时已启用）。</param>
    private sealed record PendingSetup(Guid Id, string ProtectedSecret, string SecurityStamp, bool Replacing);
}
#endif
