#if (LocalIdentity)
using CompanyName.ProjectName.Application.Auth.SignIn;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Domain.Auth.Errors;
using CompanyName.ProjectName.Domain.Users.DomainServices;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.Errors;
using CompanyName.ProjectName.Domain.Users.ValueObjects;
using Leistd.ExceptionHandling;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.Security.Claims;
using Leistd.Security.Users;
using Leistd.Timing;

namespace CompanyName.ProjectName.Application.Auth.Policies;

/// <summary>再认证的失败约束：已登录的人要再证明一次自己知道口令或验证码时，失败按登录的同一套计数与锁定处理。</summary>
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
    /// 确认当前会话的主人刚刚证明过自己：会话的认证时间（<c>auth_time</c>）在 <see cref="ReauthenticationGuard.RecentAuthenticationWindow"/> 内，
    /// 或者本次出示了正确的当前口令。用在绑定验证器、换绑、管理员重置他人两步验证这类"拿到会话就能改掉凭据"的入口前。
    /// </summary>
    /// <remarks>
    /// <para>只看会话是否受限不够：要求两步验证的受限会话可能已经开着很久，判据只认真实的认证时间。
    /// 模拟登录签发的会话不算——那是发起人认证的时间，不是这个账号的主人。</para>
    /// <para>不新鲜时：有本地口令的账号以 <see cref="AuthErrorCodes.ReauthenticationRequired"/> 要求出示口令；
    /// 只经外部登录、没有本地口令的账号以 <see cref="AuthErrorCodes.SignInAgainRequired"/> 要求重新登录——
    /// 重新走一遍外部登录得到新的认证时间，就满足了同一条判据，不为它另开豁免。</para>
    /// <para>口令错误按再认证失败计数与锁定（同 <see cref="RejectAsync"/>）。</para>
    /// </remarks>
    /// <param name="user">当前会话的用户本人。</param>
    /// <param name="currentPassword">本次出示的当前口令；没有出示时为 null。</param>
    /// <param name="action">业务动作码，失败审计用，与该操作成功路径的值一致。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task EnsureRecentlyAuthenticatedAsync(
        User user,
        string? currentPassword,
        string action,
        CancellationToken cancellationToken = default);

    /// <summary>登记一次再认证失败（计数、必要时锁定、留下审计），并<b>返回该抛给调用方的异常</b>。</summary>
    /// <remarks>
    /// 返回异常而不是由调用方自己构造：<b>这一次恰好触发锁定时，答案不再是"口令不正确"而是"已锁定"</b>
    /// ——与登录页一致（见 <c>LoginLockoutTests</c>）。把这个判断留给三个调用点各写一次，
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
    IOperationRecorder operationRecorder,
    UserDomainService userDomainService,
    ICurrentUser currentUser,
    IClock clock) : IReauthenticationGuard
{
    /// <summary>会话认证时间离现在多久以内算"刚刚认证过"。</summary>
    /// <remarks>覆盖"登录后接着去设置两步验证"这一段操作；更久的会话改为当场出示口令。</remarks>
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
    public async Task EnsureRecentlyAuthenticatedAsync(
        User user,
        string? currentPassword,
        string action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Id != currentUser.Id)
        {
            throw new InvalidOperationException("Recent authentication is checked for the signed-in user only.");
        }

        await EnsureAllowedAsync(user, cancellationToken);

        if (!string.IsNullOrEmpty(currentPassword))
        {
            if (userDomainService.VerifyCurrentPassword(user, currentPassword))
            {
                return;
            }

            throw await RejectAsync(
                user,
                action,
                SecurityErrorCodes.CurrentPasswordIncorrect,
                "The current password is incorrect.",
                cancellationToken);
        }

        if (IsRecentSession())
        {
            return;
        }

        throw user.HasLocalPassword
            ? new BusinessException(AuthErrorCodes.ReauthenticationRequired, "Enter your current password to continue.")
            : new BusinessException(AuthErrorCodes.SignInAgainRequired, "Sign in again to continue.");
    }

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
