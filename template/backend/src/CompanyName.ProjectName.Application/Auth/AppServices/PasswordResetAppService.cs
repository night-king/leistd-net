using CompanyName.ProjectName.Application.Auth.Captcha;
using CompanyName.ProjectName.Application.Auth.Dtos;
using CompanyName.ProjectName.Application.Auth.EmailVerification;
using CompanyName.ProjectName.Application.Auth.Events;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Application.Auth.SecurityAlerts;
using CompanyName.ProjectName.Application.Auth.Sessions;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Domain.Auth.Errors;
using CompanyName.ProjectName.Domain.Users.DomainServices;
using CompanyName.ProjectName.Domain.Users.Repositories;
using Leistd.Ddd.Application.AppServices;
using Leistd.EventBus.Abstractions;
using Leistd.ExceptionHandling;
using Leistd.Lock.Abstractions;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.Timing;
using Leistd.UnitOfWork;
using Microsoft.Extensions.Logging;

namespace CompanyName.ProjectName.Application.Auth.AppServices;

/// <summary>忘记口令：发码与凭码设置新口令。</summary>
/// <remarks>
/// <para>匿名可达，不回显可用于枚举的信息：发码对任意邮箱同一条路、同一种响应；设置口令时码不对、
/// 账号状态不符、凭据已变一律是 <see cref="AuthErrorCodes.EmailCodeInvalid"/>。</para>
/// <para>不受自助注册开关约束：找回的是已有账号，注册策略只借用验证码的有效期、发送间隔与尝试次数。</para>
/// <para>找回成功不签发会话：开着两步验证的账号登录时仍要走第二步。</para>
/// </remarks>
internal sealed class PasswordResetAppService(
    IUserRegistrationPolicyProvider registrationPolicy,
    ICaptchaVerifier captchaVerifier,
    EmailChallengeStore emailChallengeStore,
    IUserRepository userRepository,
    UserDomainService userDomainService,
    UserAccessRevoker accessRevoker,
    IOperationRecorder operationRecorder,
    ILocalEventBus localEventBus,
    IDistributedLock distributedLock,
    IUnitOfWorkManager unitOfWorkManager,
    IClock clock,
    ILogger<PasswordResetAppService> logger) : BaseAppService, IPasswordResetAppService
{
    public async Task<EmailVerificationChallengeOutputDto> SendCodeAsync(
        SendEmailCodeInputDto input,
        CancellationToken cancellationToken = default)
    {
        if (!await captchaVerifier.VerifyAsync(input.CaptchaToken, input.CaptchaCode, cancellationToken))
        {
            throw new BusinessException(AuthErrorCodes.CaptchaInvalid, "The image captcha is incorrect or has expired.");
        }

        var policy = await registrationPolicy.GetAsync(cancellationToken);

        // 与登录、唯一索引同一口径：按原样地址找账号。锁外只读定位信息，凭据随后变了挑战只会作废
        var email = input.Email.Trim();
        var user = await userRepository.GetFirstAsync(u => u.Email == email, q => q.OrderBy(u => u.Id), cancellationToken);
        var target = user is { CanRecoverPasswordByEmail: true }
            ? new EmailCodeRecipient(user.Id, user.Email, user.SecurityStamp)
            : null;
        // 第二步只有邮箱验证码的账号不能自助找回：不发码，改发一封请它联系管理员的告知信，响应与别的邮箱一样
        var notice = user is { NeedsAdministratorToRecoverPassword: true }
            ? new EmailCodeRecipient(user.Id, user.Email, user.SecurityStamp)
            : null;

        return await emailChallengeStore.IssuePasswordResetAsync(email, target, policy, notice, cancellationToken);
    }

    /// <remarks>
    /// <para>码先在挑战锁内消费：同一个码并发提交只有一次能过。之后在目标用户的凭据锁内、新工作单元里重读用户，
    /// 核对安全版本仍是发码时那一版、邮箱没换、仍能找回，任一不符都按码无效拒绝——一次找回、本人改口令或管理员重置之后，
    /// 其余已发出的码随安全版本轮换一起作废。</para>
    /// <para>口令、临时锁定解除、操作记录与会话撤销同在锁内提交；令牌撤销放在最后（时序见 <see cref="UserAccessRevoker"/>）。
    /// 码的消费不进数据库事务：之后的提交失败时需要重新获取验证码。提醒在提交之后发出，发不出去不影响已改好的口令。</para>
    /// </remarks>
    public async Task ResetAsync(PasswordResetInputDto input, CancellationToken cancellationToken = default)
    {
        var email = input.Email.Trim();
        var proof = await emailChallengeStore.ValidatePasswordResetAsync(
            email,
            new EmailVerificationInputDto { ChallengeId = input.ChallengeId, Code = input.Code.Trim() },
            cancellationToken)
            ?? throw InvalidCode();

        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(proof.UserId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;
        using var unit = unitOfWorkManager.Begin(requiresNew: true);

        var user = await userRepository.GetFirstAsync(u => u.Id == proof.UserId, cancellationToken: cancellationToken);
        if (user is null ||
            !user.HasSecurityStamp(proof.SecurityStamp) ||
            !string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase) ||
            !user.CanRecoverPasswordByEmail)
        {
            throw InvalidCode();
        }

        userDomainService.RecoverPassword(user, input.NewPassword);
        await userRepository.UpdateAsync(user, cancellationToken);

        await localEventBus.PublishAsync(
            new SecurityAlertRequestedEvent(user.Id, new SecurityAlert(SecurityAlertKind.PasswordRecovered), clock.Now),
            cancellationToken);
        // 匿名请求：操作人为空是如实的，"什么人"由目标承载
        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.AuthPasswordRecovered,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            OperationRecordAuthorizations.EmailCodePresented,
            cancellationToken);

        // 口令可能已落在别人手里才需要找回：此前的会话与令牌全部作废，本人用新口令重新登录
        await accessRevoker.RevokeAllAsync(user.Id, keepSessionId: null, cancellationToken);
        await unit.CompleteAsync(cancellationToken);

        logger.LogInformation("Password recovered by email code (ID: {UserId})", user.Id);
    }

    private static BusinessException InvalidCode() =>
        new(AuthErrorCodes.EmailCodeInvalid, "The email verification code is incorrect or has expired.");
}
