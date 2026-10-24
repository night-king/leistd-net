#if (LocalIdentity)
using System.Text.Json;
using CompanyName.ProjectName.Application.Auth.Dtos;
using CompanyName.ProjectName.Application.Auth.Events;
using CompanyName.ProjectName.Application.Auth.Passkeys;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Application.Auth.SecurityAlerts;
using CompanyName.ProjectName.Application.Auth.Sessions;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Domain.Auth.DomainServices;
using CompanyName.ProjectName.Domain.Auth.Entities;
using CompanyName.ProjectName.Domain.Auth.Errors;
using CompanyName.ProjectName.Domain.Auth.Passkeys;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.Errors;
using Leistd.Ddd.Application.AppServices;
using Leistd.Ddd.Domain.Repositories;
using Leistd.EventBus.Abstractions;
using Leistd.ExceptionHandling;
using Leistd.Lock.Abstractions;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.Security.OneTimeCodes;
using Leistd.Security.Users;
using Leistd.Timing;
using Leistd.UnitOfWork;
using Microsoft.Extensions.Logging;

namespace CompanyName.ProjectName.Application.Auth.AppServices;

/// <inheritdoc cref="IPasskeyAppService" />
/// <remarks>
/// <para>登记分两步：先确认是本人、生成创建选项（仪式状态按"本人 + 会话"存在服务端，绑定此刻的安全版本），
/// 浏览器创建凭据后再交回校验并保存。两步之间凭据变过（改口令、增删因子）或换了会话，这次登记作废。</para>
/// <para>改写凭据的方法都在本人的凭据锁内重读用户、改写并在手动工作单元里提交；提醒在提交之后经本地事件发出。
/// 增删通行密钥都会换安全版本：在途的登录第二步与其他仪式随之作废。</para>
/// </remarks>
internal sealed class PasskeyAppService(
    IRepository<User, Guid> userRepository,
    PasskeyDomainService passkeyDomainService,
    IPasskeyProtocol passkeyProtocol,
    PasskeyCeremonyStore ceremonies,
    UserSessionDomainService userSessionDomainService,
    ILoginSecurityPolicyProvider loginSecurityPolicy,
    IReauthenticationGuard reauthenticationGuard,
    IOperationRecorder operationRecorder,
    ILocalEventBus localEventBus,
    ICurrentUser currentUser,
    IDistributedLock distributedLock,
    IUnitOfWorkManager unitOfWorkManager,
    IClock clock,
    ILogger<PasskeyAppService> logger) : BaseAppService, IPasskeyAppService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<PasskeyOutputDto>> GetListAsync(CancellationToken cancellationToken = default) =>
        [.. (await passkeyDomainService.GetListAsync(currentUser.Id!.Value, cancellationToken)).Select(ToOutput)];

    /// <inheritdoc />
    /// <remarks>
    /// 先确认是本人：拿到会话的人不能给账号添一把自己手里的通行密钥。组织要求两步验证、本人还没有独立因子的受限会话也能登记，
    /// 登记完即解除限制。部署没配依赖方时以 <see cref="AuthErrorCodes.PasskeyUnavailable"/> 拒绝。
    /// </remarks>
    public async Task<PasskeyOptionsOutputDto> BeginRegistrationAsync(
        BeginPasskeyRegistrationInputDto input,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var userId = currentUser.Id!.Value;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;

        await reauthenticationGuard.EnsureRecentAsync(
            ReauthenticationProof.Of(input.Password, input.TwoFactorCode, input.Passkey),
            OperationRecordActions.AuthTwoFactorPasskeyAdded,
            cancellationToken);

        var user = await GetCurrentUserAsync(cancellationToken);
        var existing = await passkeyDomainService.GetListAsync(user.Id, cancellationToken);
        var ceremony = passkeyProtocol.BeginRegistration(
            new PasskeyAccount(user.Id.ToByteArray(), user.Username, user.DisplayName ?? user.Username),
            existing.Select(p => new PasskeyDescriptor(p.CredentialId, p.GetTransports())));
        await ceremonies.SaveForSessionAsync(
            PasskeyCeremonyPurpose.Registration, user.Id, currentUser.GetSessionId(), ceremony.State, user.SecurityStamp, cancellationToken);
        return OptionsOutput(ceremony.Options);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 同一认证器重复登记由浏览器按排除列表挡住；别处已登记同一凭据时以 <see cref="AuthErrorCodes.PasskeyAlreadyRegistered"/> 拒绝。
    /// 本人的其他会话随之退出：与启用身份验证器同理，它们是在没有这道门时建立的。
    /// </remarks>
    public async Task<PasskeyRegistrationOutputDto> CompleteRegistrationAsync(
        CompletePasskeyRegistrationInputDto input,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var userId = currentUser.Id!.Value;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;
        using var unit = unitOfWorkManager.Begin(requiresNew: true);

        var user = await GetCurrentUserAsync(cancellationToken);
        var state = await ceremonies.TakeForSessionAsync(
            PasskeyCeremonyPurpose.Registration, user.Id, currentUser.GetSessionId(), user.SecurityStamp, cancellationToken)
            ?? throw CeremonyExpired();

        PasskeyCredentialData credential;
        try
        {
            credential = await passkeyProtocol.CompleteRegistrationAsync(state, input.Credential!.Value.GetRawText(), cancellationToken);
        }
        catch (PasskeyVerificationException exception)
        {
            logger.LogWarning(exception, "Passkey registration for user {UserId} was rejected", user.Id);
            throw VerificationFailed();
        }

        IReadOnlyList<string> codes = user.NeedsRecoveryCodes ? RecoveryCodes.Generate() : [];
        var passkey = await passkeyDomainService.RegisterAsync(
            user, credential, input.Name, codes.Count > 0 ? codes.Select(RecoveryCodes.Hash) : null, cancellationToken);
        await userRepository.UpdateAsync(user, cancellationToken);
        await userSessionDomainService.RevokeAllAsync(user.Id, currentUser.GetSessionId(), cancellationToken);

        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.AuthTwoFactorPasskeyAdded,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            OperationRecordAuthorizations.AuthenticatedSelf,
            cancellationToken);
        await localEventBus.PublishAsync(
            new SecurityAlertRequestedEvent(user.Id, new SecurityAlert(SecurityAlertKind.PasskeyAdded), clock.Now),
            cancellationToken);

        await unit.CompleteAsync(cancellationToken);
        return new PasskeyRegistrationOutputDto { Passkey = ToOutput(passkey), RecoveryCodes = codes };
    }

    /// <inheritdoc />
    /// <remarks>仪式按本人与会话保存，绑定此刻的安全版本；确认身份的守卫取出并校验，只认一次。</remarks>
    public async Task<PasskeyOptionsOutputDto> BeginReauthenticationAsync(CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var user = await GetCurrentUserAsync(cancellationToken);
        var passkeys = await passkeyDomainService.GetListAsync(user.Id, cancellationToken);
        if (passkeys.Count == 0)
        {
            throw new BusinessException(
                AuthErrorCodes.ReauthenticationMethodUnavailable,
                "This verification method isn't available for your account.");
        }

        var ceremony = passkeyProtocol.BeginAssertion(passkeys.Select(p => new PasskeyDescriptor(p.CredentialId, p.GetTransports())));
        await ceremonies.SaveForSessionAsync(
            PasskeyCeremonyPurpose.Reauthentication, user.Id, currentUser.GetSessionId(), ceremony.State, user.SecurityStamp, cancellationToken);
        return OptionsOutput(ceremony.Options);
    }

    /// <inheritdoc />
    /// <remarks>只改显示用的名称，不增减方式：不要求确认身份、不换安全版本，照样留操作记录。</remarks>
    public async Task<PasskeyOutputDto> RenameAsync(Guid id, RenamePasskeyInputDto input, CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id!.Value;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;
        using var unit = unitOfWorkManager.Begin(requiresNew: true);

        var user = await GetCurrentUserAsync(cancellationToken);
        var passkey = await passkeyDomainService.RenameAsync(user, id, input.Name, cancellationToken);
        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.AuthTwoFactorPasskeyRenamed,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            OperationRecordAuthorizations.AuthenticatedSelf,
            cancellationToken);
        await unit.CompleteAsync(cancellationToken);
        return ToOutput(passkey);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>先确认是本人：拆掉一道门同样要证明是本人。组织要求两步验证、它又是最后一个独立因子时以
    /// <see cref="AuthErrorCodes.TwoFactorRequiredByPolicy"/> 拒绝。</para>
    /// <para>最后一个独立因子去掉时恢复码随之作废；本人的其他会话随之退出。</para>
    /// </remarks>
    public async Task RemoveAsync(Guid id, RemovePasskeyInputDto input, CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id!.Value;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;

        // 先于确认身份拒绝：不存在的、或组织要求下删不得的，不该白白用掉一次验证码
        var current = await GetCurrentUserAsync(cancellationToken);
        await passkeyDomainService.GetOwnAsync(current, id, cancellationToken);
        await EnsureRemovableAsync(current, cancellationToken);
        await reauthenticationGuard.EnsureRecentAsync(
            ReauthenticationProof.Of(input.Password, input.TwoFactorCode, input.Passkey),
            OperationRecordActions.AuthTwoFactorPasskeyRemoved,
            cancellationToken);

        using var unit = unitOfWorkManager.Begin(requiresNew: true);
        var user = await GetCurrentUserAsync(cancellationToken);
        var passkey = await passkeyDomainService.GetOwnAsync(user, id, cancellationToken);
        await EnsureRemovableAsync(user, cancellationToken);
        await passkeyDomainService.RemoveAsync(user, passkey, cancellationToken);
        await userRepository.UpdateAsync(user, cancellationToken);
        await userSessionDomainService.RevokeAllAsync(user.Id, currentUser.GetSessionId(), cancellationToken);

        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.AuthTwoFactorPasskeyRemoved,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            OperationRecordAuthorizations.AuthenticatedSelf,
            cancellationToken);
        await localEventBus.PublishAsync(
            new SecurityAlertRequestedEvent(user.Id, new SecurityAlert(SecurityAlertKind.PasskeyRemoved), clock.Now),
            cancellationToken);
        await unit.CompleteAsync(cancellationToken);
    }

    private async Task EnsureRemovableAsync(User user, CancellationToken cancellationToken)
    {
        if (!user.TwoFactorEnabled && user.PasskeyCount == 1 &&
            (await loginSecurityPolicy.GetAsync(cancellationToken)).RequireTwoFactor)
        {
            throw new BusinessException(
                AuthErrorCodes.TwoFactorRequiredByPolicy,
                "Two-factor authentication is required here and cannot be turned off.");
        }
    }

    private void EnsureAvailable()
    {
        if (!passkeyProtocol.IsAvailable)
        {
            throw new BusinessException(
                AuthErrorCodes.PasskeyUnavailable,
                "Passkeys aren't available on this site right now. Contact your administrator.");
        }
    }

    private async Task<User> GetCurrentUserAsync(CancellationToken cancellationToken) =>
        await userRepository.GetByIdAsync(currentUser.Id!.Value, cancellationToken)
        ?? throw new BusinessException(UserErrorCodes.NotFound, "User does not exist.");

    internal static PasskeyOptionsOutputDto OptionsOutput(string options, string? ceremony = null)
    {
        using var document = JsonDocument.Parse(options);
        return new PasskeyOptionsOutputDto { Options = document.RootElement.Clone(), Ceremony = ceremony };
    }

    internal static BusinessException CeremonyExpired() =>
        new(AuthErrorCodes.PasskeyCeremonyExpired, "This passkey request has expired. Try again.");

    internal static BusinessException VerificationFailed() =>
        new(AuthErrorCodes.PasskeyVerificationFailed, "The passkey couldn't be verified. Try again.");

    internal static PasskeyOutputDto ToOutput(UserPasskey passkey) => new()
    {
        Id = passkey.Id,
        Name = passkey.Name,
        CreationTime = passkey.CreationTime,
        LastUsedTime = passkey.LastUsedTime,
        IsBackupEligible = passkey.IsBackupEligible,
        IsBackedUp = passkey.IsBackedUp
    };
}
#endif
