#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.Errors;
using CompanyName.ProjectName.Domain.Users.Errors;
using CompanyName.ProjectName.Application.Auth.Dtos;
using CompanyName.ProjectName.Application.Auth.Events;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Application.Auth.SecurityAlerts;
using CompanyName.ProjectName.Application.Auth.Sessions;
using CompanyName.ProjectName.Application.Auth.TwoFactor;
#if (Email)
using CompanyName.ProjectName.Application.Auth.EmailVerification;
using Leistd.Redaction;
#endif
using CompanyName.ProjectName.Domain.Auth.DomainServices;
using CompanyName.ProjectName.Domain.Auth.Passkeys;
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
    TwoFactorMethodResolver twoFactorMethods,
#if (Email)
    EmailChallengeStore emailChallengeStore,
#endif
    UserDomainService userDomainService,
    UserSessionDomainService userSessionDomainService,
    PasskeyDomainService passkeyDomainService,
    IPasskeyProtocol passkeyProtocol,
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
        var methods = await twoFactorMethods.ResolveAsync(user, cancellationToken);
        return new TwoFactorStatusOutputDto
        {
            Enabled = user.TwoFactorEnabled,
            Passkeys = [.. (await passkeyDomainService.GetListAsync(user.Id, cancellationToken)).Select(PasskeyAppService.ToOutput)],
            PasskeysAvailable = passkeyProtocol.IsAvailable,
            RecoveryCodesLeft = user.RecoveryCodesLeft,
            RequiredByPolicy = policy.RequireTwoFactor,
            PreferredMethod = user.PreferredTwoFactorMethod,
            DefaultMethod = methods.DefaultMethod,
#if (Email)
            EmailEnabled = user.EmailTwoFactorEnabled,
            EmailUnavailableReason = await twoFactorMethods.GetEmailUnavailableReasonAsync(user, cancellationToken),
            MaskedEmail = TextRedactor.RedactEmail(user.Email)
#endif
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>先确认是本人：拿到会话就能把自己的验证器绑上去的话，被盗会话能把本人挡在第二步之外。</para>
    /// <para>换绑再出示一次第二因素：现有验证器上的码，或者验证器丢了时用一个恢复码。现有验证器上的码同时算作确认身份，
    /// 守卫校验并用掉它之后这里不再校验第二次（同一步只认一次，校验两次必然自我拒绝）。恢复码在这里当场用掉，
    /// 只换得一次重新绑定的机会，新的一组恢复码要等新验证器确认后才发——恢复码不能直接换出新的恢复码，也不算确认身份。</para>
    /// <para>在本人的凭据锁内：确认身份独立提交（验证码用掉不退回），再另开工作单元重读用户、出示恢复码并提交。</para>
    /// </remarks>
    public async Task<TwoFactorSetupOutputDto> BeginSetupAsync(
        BeginTwoFactorSetupInputDto input,
        CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id!.Value;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;

        // 锁内读到的启用状态在释放前不会再变；只为选动作码与前置拒绝，业务阶段另行重读
        var current = await GetCurrentUserEntityAsync(cancellationToken);
        var replacing = current.TwoFactorEnabled;
        var usesRecoveryCode = replacing && !string.IsNullOrWhiteSpace(input.RecoveryCode);
        // 已启用又没出示任何第二因素：是"再启用一次"而不是换绑，先于确认身份拒绝，不当作一次猜错计数
        if (replacing && string.IsNullOrWhiteSpace(input.Code) && !usesRecoveryCode)
        {
            current.EnsureTwoFactorDisabled();
        }

        var action = replacing
            ? OperationRecordActions.AuthTwoFactorAuthenticatorReplaced
            : OperationRecordActions.AuthTwoFactorEnabled;
        // 出示了恢复码时验证码不参与（与登录第二步一致：都给时以恢复码为准）
        var method = await reauthenticationGuard.EnsureRecentAsync(
            ReauthenticationProof.Of(input.Password, usesRecoveryCode ? null : input.Code, input.Passkey),
            action,
            cancellationToken);

        using var unit = unitOfWorkManager.Begin(requiresNew: true);
        var user = await GetCurrentUserEntityAsync(cancellationToken);
        // 守卫已用掉现有验证器上的码：它既确认了身份，也证明了持有现有因素
        if (replacing && method != ReauthenticationMethod.TwoFactorCode)
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

        // 恢复码是账号级的：换绑时一并换新；首次启用且它是第一个独立因子时发放，已有通行密钥的人沿用原来的
        IReadOnlyList<string> codes = setup.Replacing || user.NeedsRecoveryCodes ? RecoveryCodes.Generate() : [];
        if (setup.Replacing)
        {
            user.ReplaceTwoFactor(setup.ProtectedSecret, codes.Select(RecoveryCodes.Hash), step);
        }
        else
        {
            user.EnableTwoFactor(setup.ProtectedSecret, codes.Count > 0 ? codes.Select(RecoveryCodes.Hash) : null, step);
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

        // 组织要求两步验证时不能拆掉最后一个独立因子；还有通行密钥就照常停用
        if (user.PasskeyCount == 0 && (await loginSecurityPolicy.GetAsync(cancellationToken)).RequireTwoFactor)
        {
            throw new BusinessException(AuthErrorCodes.TwoFactorRequiredByPolicy, "Two-factor authentication is required here and cannot be turned off.");
        }

        // 停用两步验证是再认证：锁定期内不给试，失败按登录的同一套计数。
        // 少了这一道，持有被盗会话的人能在这个接口上无限次猜——猜到了就把这个账号的第二道防线拆了。
        // 有口令的账号口令与验证码各过一关；没有口令（只经外部登录）的账号只过验证码这一关，
        // 不能拿一个不存在的口令去比对并计入失败——那样本人永远停用不了，反复尝试还会把自己锁住
        await reauthenticationGuard.EnsureAllowedAsync(user, cancellationToken);

        if (user.HasLocalPassword)
        {
            if (string.IsNullOrEmpty(input.Password))
            {
                throw new BusinessException(SecurityErrorCodes.PasswordRequired, "Password cannot be empty.");
            }

            if (!userDomainService.VerifyCurrentPassword(user, input.Password))
            {
                throw await reauthenticationGuard.RejectAsync(
                    user,
                    OperationRecordActions.AuthTwoFactorDisabled,
                    SecurityErrorCodes.CurrentPasswordIncorrect,
                    "The current password is incorrect.",
                    cancellationToken);
            }
        }
        else if (!string.IsNullOrEmpty(input.Password))
        {
            throw new BusinessException(
                AuthErrorCodes.ReauthenticationMethodUnavailable,
                "This verification method isn't available for your account.");
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
        RegenerateRecoveryCodesInputDto input,
        CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id!.Value;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;
        using var unit = unitOfWorkManager.Begin(requiresNew: true);

        var user = await GetCurrentUserEntityAsync(cancellationToken);
        // 先于再认证拒绝：没有独立因子就没有恢复码，也没有可校验的凭证，不该计入失败次数
        if (!user.HasIndependentFactor)
        {
            throw new BusinessException(AuthErrorCodes.TwoFactorNotEnabled, "Two-factor authentication is not turned on.");
        }

        // 重发恢复码同样是再认证：拿到恢复码等于拿到一组可绕过两步验证的凭据。
        // 只认独立因子（身份验证器验证码或通行密钥），不认口令与恢复码：恢复码不能用来换出一组新的恢复码
        if (input.Passkey is { } passkey)
        {
            // 通行密钥的仪式与校验收在守卫里；守卫在自己的工作单元里提交，这里重读用户再改
            await reauthenticationGuard.EnsureRecentAsync(
                ReauthenticationProof.Of(null, null, passkey),
                OperationRecordActions.AuthTwoFactorRecoveryCodesRegenerated,
                cancellationToken);
            user = await GetCurrentUserEntityAsync(cancellationToken);
        }
        else
        {
            await reauthenticationGuard.EnsureAllowedAsync(user, cancellationToken);
            if (!twoFactorDomainService.VerifyCode(user, input.Code!, clock.Now))
            {
                throw await reauthenticationGuard.RejectAsync(
                    user,
                    OperationRecordActions.AuthTwoFactorRecoveryCodesRegenerated,
                    AuthErrorCodes.TwoFactorCodeInvalid,
                    CodeInvalidMessage,
                    cancellationToken);
            }
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

#if (Email)
    /// <inheritdoc />
    /// <remarks>
    /// <para>开启前先确认是本人，再把确认码发到当前邮箱：拿到会话的人不能悄悄给账号加一种自己收得到的第二步方式，
    /// 也不能开在一个本人收不到信的地址上。已开启或此刻不能用时先于确认身份拒绝，不白白用掉验证码。</para>
    /// <para>在本人的凭据锁内：确认身份独立提交（验证码用掉不退回），之后重读用户再发码。</para>
    /// </remarks>
    public async Task<EmailVerificationChallengeOutputDto> BeginEmailSetupAsync(
        EmailTwoFactorChangeInputDto input,
        CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id!.Value;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;

        await EnsureEmailCanBeEnabledAsync(await GetCurrentUserEntityAsync(cancellationToken), cancellationToken);
        await reauthenticationGuard.EnsureRecentAsync(
            ReauthenticationProof.Of(input.Password, input.TwoFactorCode, input.Passkey),
            OperationRecordActions.AuthTwoFactorEmailEnabled,
            cancellationToken);

        var user = await GetCurrentUserEntityAsync(cancellationToken);
        await EnsureEmailCanBeEnabledAsync(user, cancellationToken);
        return await emailChallengeStore.IssueTwoFactorEmailSetupAsync(
            new EmailCodeRecipient(user.Id, user.Email, user.SecurityStamp), cancellationToken);
    }

    /// <inheritdoc />
    public async Task EnableEmailAsync(EmailVerificationInputDto input, CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id!.Value;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;
        using var unit = unitOfWorkManager.Begin(requiresNew: true);

        var user = await GetCurrentUserEntityAsync(cancellationToken);
        await EnsureEmailCanBeEnabledAsync(user, cancellationToken);
        // 确认码绑定发码时的账号、地址与安全版本：之后换过邮箱或凭据变过，这个码就不再算数
        var proof = await emailChallengeStore.ValidateTwoFactorEmailSetupAsync(
            user.Email,
            input with { Code = input.Code.Trim() },
            cancellationToken);
        if (proof is null || proof.UserId != user.Id || !user.HasSecurityStamp(proof.SecurityStamp))
        {
            throw new BusinessException(AuthErrorCodes.EmailCodeInvalid, "The email verification code is incorrect or has expired.");
        }

        user.EnableEmailTwoFactor();
        await userRepository.UpdateAsync(user, cancellationToken);
        // 登录方式变了：与启用身份验证器同理，其他会话是在没有这道门时建立的
        await userSessionDomainService.RevokeAllAsync(user.Id, currentUser.GetSessionId(), cancellationToken);

        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.AuthTwoFactorEmailEnabled,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            OperationRecordAuthorizations.AuthenticatedSelf,
            cancellationToken);
        await localEventBus.PublishAsync(
            new SecurityAlertRequestedEvent(user.Id, new SecurityAlert(SecurityAlertKind.TwoFactorEmailEnabled), clock.Now),
            cancellationToken);

        await unit.CompleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>关闭等于少一种第二步方式，同样先确认是本人；本人的其他会话随之退出。组织要求两步验证时也能关：邮箱验证码本就不算满足要求。</remarks>
    public async Task DisableEmailAsync(EmailTwoFactorChangeInputDto input, CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id!.Value;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;

        if (!(await GetCurrentUserEntityAsync(cancellationToken)).EmailTwoFactorEnabled)
            return;

        await reauthenticationGuard.EnsureRecentAsync(
            ReauthenticationProof.Of(input.Password, input.TwoFactorCode, input.Passkey),
            OperationRecordActions.AuthTwoFactorEmailDisabled,
            cancellationToken);

        using var unit = unitOfWorkManager.Begin(requiresNew: true);
        var user = await GetCurrentUserEntityAsync(cancellationToken);
        user.DisableEmailTwoFactor();
        await userRepository.UpdateAsync(user, cancellationToken);
        await userSessionDomainService.RevokeAllAsync(user.Id, currentUser.GetSessionId(), cancellationToken);

        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.AuthTwoFactorEmailDisabled,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            OperationRecordAuthorizations.AuthenticatedSelf,
            cancellationToken);
        await localEventBus.PublishAsync(
            new SecurityAlertRequestedEvent(user.Id, new SecurityAlert(SecurityAlertKind.TwoFactorEmailDisabled), clock.Now),
            cancellationToken);

        await unit.CompleteAsync(cancellationToken);
    }

    // 能开启的前提与登录时能用的前提相同：组织允许、能发码、邮箱已验证；已开启时告诉本人"已经开着"
    private async Task EnsureEmailCanBeEnabledAsync(User user, CancellationToken cancellationToken)
    {
        if (user.EmailTwoFactorEnabled)
        {
            throw new BusinessException(AuthErrorCodes.TwoFactorAlreadyEnabled, "Email verification codes are already turned on.");
        }

        switch (await twoFactorMethods.GetEmailUnavailableReasonAsync(user, cancellationToken))
        {
            case TwoFactorEmailUnavailableReason.NotAllowed:
                throw new BusinessException(AuthErrorCodes.TwoFactorMethodUnavailable, "Your organization doesn't allow email verification codes for signing in.");
            case TwoFactorEmailUnavailableReason.DeliveryUnavailable:
                throw new BusinessException(AuthErrorCodes.EmailVerificationUnavailable, "Email verification is temporarily unavailable. Please contact your administrator.");
            case TwoFactorEmailUnavailableReason.EmailNotVerified:
                user.EnsureEmailUsableForTwoFactor();
                break;
        }
    }
#endif

    /// <inheritdoc />
    /// <remarks>只换登录时先给出哪一种，不增减方式，所以不要求确认身份，也不换安全版本；照样留操作记录，本人能查到自己改过。</remarks>
    public async Task SetPreferredMethodAsync(
        SetPreferredTwoFactorMethodInputDto input,
        CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id!.Value;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;
        using var unit = unitOfWorkManager.Begin(requiresNew: true);

        var user = await GetCurrentUserEntityAsync(cancellationToken);
        user.SetPreferredTwoFactorMethod(input.Method!.Value);
        await userRepository.UpdateAsync(user, cancellationToken);
        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.AuthTwoFactorPreferredMethodChanged,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            OperationRecordAuthorizations.AuthenticatedSelf,
            cancellationToken);
        await unit.CompleteAsync(cancellationToken);
    }

    private const string CodeInvalidMessage = "The verification code is incorrect.";

    private static BusinessException CodeInvalid() =>
        new(AuthErrorCodes.TwoFactorCodeInvalid, CodeInvalidMessage);

    /// <summary>待确认设置密钥的缓存键。</summary>
    internal static string SetupKey(Guid userId) => SetupKeyPrefix + userId.ToString("N");

    /// <summary>换绑前用恢复码出示现有的第二因素（验证码已由确认身份那一步校验并用掉）；失败按再认证计数。用掉的恢复码随本次工作单元落库。</summary>
    private async Task ProveCurrentSecondFactorAsync(
        User user,
        BeginTwoFactorSetupInputDto input,
        string action,
        CancellationToken cancellationToken)
    {
        var usedRecoveryCode = !string.IsNullOrWhiteSpace(input.RecoveryCode);
        var proven = usedRecoveryCode && twoFactorDomainService.UseRecoveryCode(user, input.RecoveryCode!);
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
