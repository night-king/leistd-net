#if (LocalIdentity)
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CompanyName.ProjectName.Application.Auth.AppServices;
using CompanyName.ProjectName.Application.Auth.Passkeys;
using CompanyName.ProjectName.Application.Auth.Sessions;
using CompanyName.ProjectName.Domain.Auth.Passkeys;
using CompanyName.ProjectName.Application.Auth.SignIn;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Domain.Auth.Errors;
using CompanyName.ProjectName.Domain.Users.DomainServices;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.Errors;
using CompanyName.ProjectName.Domain.Users.ValueObjects;
using Leistd.Ddd.Domain.Repositories;
using Leistd.ExceptionHandling;
using Leistd.Lock.Abstractions;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.Security.Claims;
using Leistd.Security.Users;
using Leistd.Timing;
using Leistd.UnitOfWork;

namespace CompanyName.ProjectName.Application.Auth.Policies;

/// <summary>本次确认身份出示的凭证：当前口令、操作者本人身份验证器上的验证码或通行密钥断言，一次只出示一种；都为空表示只看会话是否刚认证过。</summary>
/// <param name="CurrentPassword">操作者本人的当前口令。</param>
/// <param name="TwoFactorCode">操作者本人身份验证器上的 6 位验证码。</param>
/// <param name="Passkey">操作者本人通行密钥断言的 JSON 原文（先经确认身份的选项端点取选项）。</param>
internal sealed record ReauthenticationProof(string? CurrentPassword, string? TwoFactorCode, string? Passkey = null)
{
    /// <summary>没有出示任何凭证。</summary>
    public static ReauthenticationProof None { get; } = new(null, null);

    /// <summary>由入参的三种凭证组成；通行密钥取断言原文。</summary>
    public static ReauthenticationProof Of(string? currentPassword, string? twoFactorCode, JsonElement? passkey) =>
        new(currentPassword, twoFactorCode, passkey is { ValueKind: JsonValueKind.Object } value ? value.GetRawText() : null);

    /// <summary>出示了通行密钥。</summary>
    public bool HasPasskey => !string.IsNullOrEmpty(Passkey);

    /// <summary>出示了口令。</summary>
    public bool HasPassword => !string.IsNullOrEmpty(CurrentPassword);

    /// <summary>出示了验证码。</summary>
    public bool HasTwoFactorCode => !string.IsNullOrWhiteSpace(TwoFactorCode);

    /// <summary>入参 DTO 的互斥校验：出示了不止一种时返回字段错误（文案是本地化键）。</summary>
    public static IEnumerable<ValidationResult> ValidateOneAtATime(
        string? currentPassword,
        string? twoFactorCode,
        JsonElement? passkey,
        string twoFactorCodeMember)
    {
        var presented = (string.IsNullOrEmpty(currentPassword) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(twoFactorCode) ? 0 : 1)
            + (passkey is { ValueKind: JsonValueKind.Object } ? 1 : 0);
        if (presented > 1)
        {
            yield return new ValidationResult(
                "Confirm with only one of your password, a verification code, or a passkey.", [twoFactorCodeMember]);
        }
    }
}

/// <summary>这一次确认身份靠什么通过。</summary>
internal enum ReauthenticationMethod
{
    /// <summary>会话的认证时间在窗口内，没有出示凭证。</summary>
    RecentSignIn,

    /// <summary>出示了正确的当前口令。</summary>
    Password,

    /// <summary>出示了正确的验证码；这一步已用掉并提交，不能再用。</summary>
    TwoFactorCode,

    /// <summary>出示了通过校验的通行密钥断言。</summary>
    Passkey
}

/// <summary>再认证：已登录的人改动"谁能登进这个账号"之前，再证明一次是本人；失败按登录的同一套计数与锁定处理。</summary>
/// <remarks>
/// <para><b>为什么需要这一层。</b>登录页有锁定，再认证入口（改口令、停用两步验证、重发恢复码）
/// 如果没有，那条"连续失败 N 次锁定"的设置就名不副实：会话被盗用的人能在改口令接口上
/// <b>无限次</b>猜当前口令，把登录页的锁定整个绕过去。这类再认证失败按 OWASP ASVS 与
/// NIST 800-63B 都应当既记日志、又与登录共用失败计数。</para>
/// <para><b>为什么不新造一个计数器。</b>计数与锁定走 <see cref="IAccessFailureCounter"/>，
/// 与口令登录、两步验证登录同一份实现；管理员调 <c>Security.LockoutMaxFailedAttempts</c> 时
/// 才不会出现"有的地方算、有的地方不算"。</para>
/// <para><b><see cref="EnsureAllowedAsync"/> 不能省。</b>临时锁定按设计只挡新登录、不踢已有会话
/// （见 <see cref="User.AllowsExistingSessions"/>）。只计数不拦的话，攻击者猜到锁定为止、
/// 自己手里那个被盗会话照常用、接着猜，结果只是把本人挡在登录页外——比不改更糟。</para>
/// </remarks>
internal interface IReauthenticationGuard
{
    /// <summary>锁定期内直接拒绝，不进入校验。必须在比对口令或验证码<b>之前</b>调用。</summary>
    Task EnsureAllowedAsync(User user, CancellationToken cancellationToken = default);

    /// <summary>
    /// 确认当前会话的主人刚刚证明过自己：没有出示凭证时看会话的认证时间（<c>auth_time</c>）是否在
    /// <see cref="ReauthenticationGuard.RecentAuthenticationWindow"/> 内；出示了凭证时只按这份凭证判定。
    /// 用在改邮箱、设置口令、绑定验证器与换绑、绑定与解绑外部账号、管理员改他人凭据这类"拿到会话就能改掉凭据"的入口前。
    /// </summary>
    /// <remarks>
    /// <para><b>调用方必须已持有操作者本人的凭据锁</b>（<see cref="UserCredentialLock"/>）；管理员入口与绑定预检用
    /// <see cref="EnsureRecentInOwnLockAsync"/>。本方法在独立工作单元里重读操作者、校验并提交：验证码通过即作废，
    /// 之后业务失败也不退回；调用方的业务阶段要另开工作单元重新读取用户，不能沿用此前读到的实体。</para>
    /// <para>判定顺序：锁定期内先拒绝。出示了凭证时不回落到会话时间或另一种凭证：本账号不具备这种方式
    /// （没有口令却给口令、没开两步验证却给验证码）以 <see cref="AuthErrorCodes.ReauthenticationMethodUnavailable"/> 拒绝且不计数；
    /// 凭证错误按再认证失败计数与锁定（同 <see cref="RejectAsync"/>）。没有出示凭证而会话不够新时，没有口令也没开两步验证的账号
    /// 以 <see cref="AuthErrorCodes.SignInAgainRequired"/> 要求重新登录，其余以 <see cref="AuthErrorCodes.ReauthenticationRequired"/>
    /// 要求出示凭证。只看会话是否受限不够：受限会话可能已经开着很久；模拟登录签发的会话不算刚认证——那是发起人认证的时间。</para>
    /// <para>只对当次操作有效：不改写会话的认证时间，不开宽限期。</para>
    /// </remarks>
    /// <param name="proof">本次出示的凭证，一次只能一种。</param>
    /// <param name="action">业务动作码，失败审计用，与该操作成功路径的值一致。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>通过的方式；为 <see cref="ReauthenticationMethod.TwoFactorCode"/> 时这一步已用掉，换绑据此不再校验第二次。</returns>
    Task<ReauthenticationMethod> EnsureRecentAsync(
        ReauthenticationProof proof,
        string action,
        CancellationToken cancellationToken = default);

    /// <summary>取操作者本人的凭据锁，在锁内执行 <see cref="EnsureRecentAsync"/>，提交后释放。</summary>
    /// <remarks>
    /// 管理员操作他人与绑定外部账号的预检用：验证阶段只持操作者的锁，释放之后调用方再取目标（或票据）的锁进入业务阶段，
    /// 两位管理员互相操作时不会交叉持锁；操作者就是目标时同一把锁先后取两次，不嵌套。
    /// </remarks>
    Task<ReauthenticationMethod> EnsureRecentInOwnLockAsync(
        ReauthenticationProof proof,
        string action,
        CancellationToken cancellationToken = default);

    /// <summary>登记一次再认证失败（计数、必要时锁定、留下审计），并<b>返回该抛给调用方的异常</b>。</summary>
    /// <remarks>
    /// 返回异常而不是由调用方自己构造：<b>这一次恰好触发锁定时，答案不再是"口令不正确"而是"已锁定"</b>
    /// ——与登录页一致（见 <c>LoginLockoutTests</c>）。把这个判断留给各调用点各写一次，
    /// 漏掉的那个会在锁定后继续回"口令不正确"，让人以为还能再试。
    /// 用法固定是 <c>throw await guard.RejectAsync(...)</c>。
    /// </remarks>
    /// <param name="user">当前用户。</param>
    /// <param name="action">业务动作码，与该操作成功路径使用的值逐字一致。</param>
    /// <param name="failureCode">失败原因码。</param>
    /// <param name="failureMessage">未触发锁定时向调用方公开的安全文案。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<BusinessException> RejectAsync(
        User user,
        string action,
        string failureCode,
        string failureMessage,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IReauthenticationGuard" />
internal sealed class ReauthenticationGuard(
    IAccessFailureCounter accessFailureCounter,
    IPasskeyProtocol passkeyProtocol,
    PasskeyCeremonyStore passkeyCeremonies,
    PasskeyAssertionVerifier passkeyVerifier,
    IOperationRecorder operationRecorder,
    UserDomainService userDomainService,
    TwoFactorDomainService twoFactorDomainService,
    IRepository<User, Guid> userRepository,
    IUnitOfWorkManager unitOfWorkManager,
    IDistributedLock distributedLock,
    ICurrentUser currentUser,
    IClock clock) : IReauthenticationGuard
{
    /// <summary>会话认证时间离现在多久以内算"刚刚认证过"。</summary>
    /// <remarks>覆盖"登录后接着去设置两步验证"这一段操作；更久的会话改为当场出示凭证。</remarks>
    public static readonly TimeSpan RecentAuthenticationWindow = TimeSpan.FromMinutes(15);

    /// <inheritdoc />
    public async Task EnsureAllowedAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var now = clock.Now;
        if (user.GetAccessStatus(now) == UserAccessStatus.LockedOut)
        {
            throw SessionIssuer.LockedOut(user, now);
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<BusinessException> RejectAsync(
        User user,
        string action,
        string failureCode,
        string failureMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var outcome = await accessFailureCounter.CountAsync(user.Id, cancellationToken);
        if (outcome.User is not { } counted)
        {
            // 行在本次请求中途被删了：没有计数，也没有可留审计的目标。按原样拒绝即可，
            // 会话的去留由下一次请求的会话校验处理。
            return new BusinessException(failureCode, failureMessage);
        }

        await operationRecorder.RecordFailedAsync(
            action,
            OperationTarget.For(counted.Id, counted.DisplayName ?? counted.Username),
            // 与登录侧不同：此刻主体已建立，是本人在对自己做这件事
            OperationRecordAuthorizations.AuthenticatedSelf,
            OperationFailure.FromCode(failureCode));

        if (!outcome.LockoutTriggered)
        {
            return new BusinessException(failureCode, failureMessage);
        }

        await accessFailureCounter.RecordLockoutAsync(
            counted, OperationRecordAuthorizations.AuthenticatedSelf, cancellationToken);

        // 取刚提交的那一行，不是调用方手里的陈旧实例
        return SessionIssuer.LockedOut(counted, clock.Now);
    }

    /// <inheritdoc />
    public async Task<ReauthenticationMethod> EnsureRecentAsync(
        ReauthenticationProof proof,
        string action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var userId = currentUser.Id
            ?? throw new InvalidOperationException("Recent authentication is checked for a signed-in user only.");

        // 独立工作单元：读取此刻最新的操作者，验证码用掉的步随它提交，与调用方随后的业务提交无关
        using var unit = unitOfWorkManager.Begin(requiresNew: true);
        var user = await userRepository.GetByIdAsync(userId, cancellationToken)
            ?? throw new BusinessException(UserErrorCodes.NotFound, $"User {userId} not found.").WithData("Id", userId);

        var method = await VerifyAsync(user, proof, action, cancellationToken);
        if (method == ReauthenticationMethod.TwoFactorCode)
        {
            // 用掉的步必须落库，否则同一个码还能在别的入口或登录第二步再用一次
            await userRepository.UpdateAsync(user, cancellationToken);
        }

        await unit.CompleteAsync(cancellationToken);
        return method;
    }

    /// <inheritdoc />
    public async Task<ReauthenticationMethod> EnsureRecentInOwnLockAsync(
        ReauthenticationProof proof,
        string action,
        CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id
            ?? throw new InvalidOperationException("Recent authentication is checked for a signed-in user only.");
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        return await EnsureRecentAsync(proof, action, lockScope.Token);
    }

    /// <summary>判定表本身：对已读出的操作者按凭证或会话时间判定。验证码通过时改动了 <paramref name="user"/>（记下用掉的步），由调用方保存。</summary>
    internal async Task<ReauthenticationMethod> VerifyAsync(
        User user,
        ReauthenticationProof proof,
        string action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Id != currentUser.Id)
        {
            throw new InvalidOperationException("Recent authentication is checked for the signed-in user only.");
        }

        if ((proof.HasPassword ? 1 : 0) + (proof.HasTwoFactorCode ? 1 : 0) + (proof.HasPasskey ? 1 : 0) > 1)
        {
            // 入参 DTO 已以字段错误拒绝；走到这里是调用方拼错了凭证
            throw new ArgumentException("Present one proof at a time.", nameof(proof));
        }

        await EnsureAllowedAsync(user, cancellationToken);

        if (proof.HasPassword)
        {
            // 没有本地口令就没什么可猜的：不计数，否则别人能借此把只用外部登录的账号锁住
            if (!user.HasLocalPassword)
            {
                throw MethodUnavailable();
            }

            if (userDomainService.VerifyCurrentPassword(user, proof.CurrentPassword!))
            {
                return ReauthenticationMethod.Password;
            }

            throw await RejectAsync(
                user,
                action,
                SecurityErrorCodes.CurrentPasswordIncorrect,
                "The current password is incorrect.",
                cancellationToken);
        }

        if (proof.HasTwoFactorCode)
        {
            if (!user.TwoFactorEnabled)
            {
                throw MethodUnavailable();
            }

            // 与登录第二步同一个判定：同一步只认一次，登录刚用过的码在这里同样被拒
            if (twoFactorDomainService.VerifyCode(user, proof.TwoFactorCode!, clock.Now))
            {
                return ReauthenticationMethod.TwoFactorCode;
            }

            throw await RejectAsync(
                user,
                action,
                AuthErrorCodes.TwoFactorCodeInvalid,
                "The verification code is incorrect.",
                cancellationToken);
        }

        if (proof.HasPasskey)
        {
            if (user.PasskeyCount == 0 || !passkeyProtocol.IsAvailable)
            {
                throw MethodUnavailable();
            }

            // 仪式按本人与会话保存：别的会话取的选项、凭据变过之后的选项都不认，不算猜错
            var state = await passkeyCeremonies.TakeForSessionAsync(
                PasskeyCeremonyPurpose.Reauthentication, user.Id, currentUser.GetSessionId(), user.SecurityStamp, cancellationToken)
                ?? throw PasskeyAppService.CeremonyExpired();
            var passkey = await passkeyVerifier.FindAsync(proof.Passkey!, cancellationToken);
            if (passkey is not null && passkey.UserId == user.Id &&
                await passkeyVerifier.VerifyAsync(passkey, state, proof.Passkey!, cancellationToken))
            {
                return ReauthenticationMethod.Passkey;
            }

            throw await RejectAsync(
                user,
                action,
                AuthErrorCodes.PasskeyVerificationFailed,
                "The passkey couldn't be verified. Try again.",
                cancellationToken);
        }

        if (IsRecentSession())
        {
            return ReauthenticationMethod.RecentSignIn;
        }

        // 口令、身份验证器、通行密钥都没有：拿不出任何凭证，只能重新走一遍外部登录得到新的认证时间
        throw !user.HasLocalPassword && !user.TwoFactorEnabled && user.PasskeyCount == 0
            ? new BusinessException(AuthErrorCodes.SignInAgainRequired, "Sign in again to continue.")
            : new BusinessException(AuthErrorCodes.ReauthenticationRequired, "Confirm it's you to continue.");
    }

    private static BusinessException MethodUnavailable() =>
        new(AuthErrorCodes.ReauthenticationMethodUnavailable, "This verification method isn't available for your account.");

    private bool IsRecentSession()
    {
        if (currentUser.FindClaim(CustomClaimTypes.ImpersonatorUserId) is not null)
        {
            return false;
        }

        if (SessionIssuer.ReadAuthenticationTime(currentUser.FindClaim(SessionIssuer.AuthenticationTimeClaimType)) is not { } authenticatedAt)
        {
            return false;
        }

        var now = new DateTimeOffset(DateTime.SpecifyKind(clock.Now, DateTimeKind.Utc));
        return now - authenticatedAt <= RecentAuthenticationWindow;
    }
}
#endif
