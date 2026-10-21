using Leistd.Data.Querying;
using CompanyName.ProjectName.Application.Auth.TwoFactor;
#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.Errors;
#endif
using CompanyName.ProjectName.Domain.Auth.DomainServices;
using CompanyName.ProjectName.Domain.Auth.Entities;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Application.Auth.SecurityAlerts;
using CompanyName.ProjectName.Domain.Users.Repositories;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.ValueObjects;
using Leistd.Ddd.Domain.Repositories;
using Leistd.Security.Claims;
using Leistd.Timing;
using Leistd.ExceptionHandling;
#if (Impersonation)
using CompanyName.ProjectName.Application.Auth.Constants;
#endif
using CompanyName.ProjectName.Application.Shared;
using Leistd.Security.RequestContext;
using System.Globalization;

namespace CompanyName.ProjectName.Application.Auth.SignIn;

/// <summary>签发登录会话：判定账号可登录、记录登录、建立会话并组装主体；需要第二步时只发两步验证挑战。</summary>
internal sealed class SessionIssuer(
    IUserRepository userRepository,
    UserSessionDomainService userSessionDomainService,
    IRepository<UserSession, Guid> userSessionRepository,
    IQueryableAsyncExecuter asyncExecuter,
    TwoFactorChallengeStore twoFactorChallengeStore,
    ILoginSecurityPolicyProvider loginSecurityPolicy,
    ISecurityAlertPublisher securityAlerts,
    IRequestClientInfo clientInfo,
    IOptions<ClaimTypeOptions> claimTypes,
    IClock clock)
{
    // OIDC 标准声明名，ICurrentUser 按它们分别读用户名与显示名。
    private const string PreferredUsernameClaimType = "preferred_username";
    private const string DisplayNameClaimType = "name";
    // OIDC 标准角色声明名。身份的 RoleClaimType 必须随之设为它，
    // 否则官方 IsInRole / RequireRole 按默认的 ClaimTypes.Role 去找，静默判为不在角色中。
    private const string RoleClaimType = "role";

    /// <summary>会话主体上记录实际认证时刻的 OIDC 标准声明名。</summary>
    public const string AuthenticationTimeClaimType = "auth_time";

    /// <summary>读出主体的实际认证时刻；没有或格式不对时返回 null。</summary>
    public static DateTimeOffset? ReadAuthenticationTime(Claim? claim) =>
        long.TryParse(claim?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    /// <summary>登录第一步（密码或外部登录）通过之后决定去向。</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>已启用两步验证：不签发会话，只发第二步凭据。</item>
    /// <item>否则直接签发会话。组织要求两步验证而本人未启用时会话是否受限不在这里决定、也不写进会话，
    /// 由每个请求的会话校验按当时的策略判定（见 <see cref="Sessions.IUserSessionValidator"/>）。</item>
    /// </list>
    /// 账号是否可登录（停用、锁定）在发第二步凭据之前就判：否则停用的账号也能走到输验证码那一步。
    /// </remarks>
    /// <param name="user">第一步已认出的用户。</param>
    /// <param name="roleNames">同 <see cref="SignInAsync"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SessionLoginResult> StartAsync(
        User user,
        List<string>? roleNames = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAllowed(user, clock.Now);

        if (user.TwoFactorEnabled)
        {
            return SessionLoginResult.TwoFactorRequired(
                await twoFactorChallengeStore.CreateAsync(user.Id, user.TenantId, user.SecurityStamp, cancellationToken));
        }

        return SessionLoginResult.SignedIn(
            await SignInAsync(user, roleNames, cancellationToken: cancellationToken));
    }

    /// <param name="user">登录用户。</param>
    /// <param name="roleNames">
    /// 调用方已知的角色名。为 <see langword="null"/> 时回查数据库。
    /// 首次外部登录必须传入：此时角色关联行与用户同在一个未提交的边界内，回查得到空集合，
    /// 签发出的主体会少掉全部角色。
    /// </param>
    /// <param name="additionalClaims">
    /// 追加到主体上的额外声明（模拟登录的发起人声明）。会话主体是这些声明的唯一载体——
    /// 已认证请求的租户由 cookie claim 定案，请求头改写不了，所以模拟态也必须落在这里。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="authenticatedAt">
    /// 这次会话对应的实际认证时刻，写进 <c>auth_time</c>。只有真正出示了凭据的那一刻（登录、第二步通过）才是"现在"，
    /// 此时不传；只是换发会话的调用方（进入或退出模拟）传入原会话的认证时刻，
    /// 读不出时传 <see cref="DateTimeOffset.UnixEpoch"/>——宁可让依赖它的判据（近期认证、OIDC <c>max_age</c>）要求重新认证，
    /// 也不能把一次会话切换当成刚认证过。
    /// </param>
    /// <remarks>
    /// 模拟登录复用本方法，因此 <c>RecordLoginSuccess</c> 也会记在被模拟的账号上：
    /// 换来的是访问校验（禁用、锁定）只有一条实现，不会出现"正常登录拒绝、模拟登录放行"的偏差。
    /// 代价是该账号的"最近登录"含模拟进入的时刻，排查登录异常时需要结合审计日志区分。
    /// <para>每次签发都登记一个会话（见 <see cref="UserSessionDomainService"/>），会话 Id 写进 <c>sid</c> 声明。
    /// 登记跟随调用方的租户上下文与工作单元：模拟登录在目标租户的工作单元里调用，会话就落在那个租户的库里。</para>
    /// </remarks>
    public async Task<ClaimsPrincipal> SignInAsync(
        User user,
        List<string>? roleNames = null,
        IEnumerable<Claim>? additionalClaims = null,
        CancellationToken cancellationToken = default,
        DateTimeOffset? authenticatedAt = null)
    {
        var now = clock.Now;
        EnsureAllowed(user, now);

        var previousIp = user.LastLogin?.Ip;
        user.RecordLoginSuccess(now, clientInfo.IpAddress);
        await userRepository.UpdateAsync(user, cancellationToken);

        var extraClaims = additionalClaims?.ToList() ?? [];
        var impersonatorName = ReadImpersonatorName(extraClaims);
        // 空闲时限按会话所在的租户（模拟登录时是目标租户）
        var idleTimeout = (await loginSecurityPolicy.GetAsync(cancellationToken)).SessionIdleTimeout;
        var newDevice = await IsNewDeviceAsync(user.Id, previousIp, impersonatorName, idleTimeout, cancellationToken);
        var session = await userSessionDomainService.StartAsync(
            user.Id, clientInfo.IpAddress, clientInfo.UserAgent, impersonatorName, idleTimeout, cancellationToken);
        if (newDevice)
        {
            await securityAlerts.PublishAsync(
                user.Id,
                new SecurityAlert(SecurityAlertKind.NewDeviceSignIn, clientInfo.IpAddress, clientInfo.UserAgent),
                cancellationToken);
        }

        // 真正认证时记录，Cookie 滑动续期与会话换发都不能更新 OIDC max_age 与近期认证的基准。
        var authenticationTime = authenticatedAt ?? new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));
        return await BuildPrincipalAsync(user, session.Id, authenticationTime, roleNames, extraClaims, cancellationToken);
    }

    private async Task<ClaimsPrincipal> BuildPrincipalAsync(
        User user,
        Guid sessionId,
        DateTimeOffset authenticationTime,
        List<string>? roleNames,
        List<Claim> extraClaims,
        CancellationToken cancellationToken)
    {
        var identity = new ClaimsIdentity(AuthenticationSchemeNames.SessionCookie, ClaimTypes.Name, RoleClaimType);
        identity.AddClaim(new Claim(claimTypes.Value.UserIds[0], user.Id.ToString()));
        identity.AddClaim(new Claim(CustomClaimTypes.SessionId, sessionId.ToString()));
        identity.AddClaim(new Claim(AuthenticationTimeClaimType,
            authenticationTime.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.Username));
        // ICurrentUser 的约定：Username 取 preferred_username，Name 是显示名、取 name。
        // 只写 ClaimTypes.Name 时两者都读到登录名，操作记录的操作人列就与目标列口径不一——
        // 同一个人在这边是 "admin"，在那边（目标名按 DisplayName ?? Username）是 "System Administrator"。
        // ClaimTypes.Name 保持登录名不动：它是 Identity.Name，框架外的中间件按它认人。
        identity.AddClaim(new Claim(PreferredUsernameClaimType, user.Username));
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            identity.AddClaim(new Claim(DisplayNameClaimType, user.DisplayName));
        }
        identity.AddClaim(new Claim(CustomClaimTypes.IsSuperAdmin, user.IsSuperAdmin ? "true" : "false"));

        if (user.TenantId is { } tenantId)
        {
            identity.AddClaim(new Claim(claimTypes.Value.TenantId, tenantId.ToString()));
        }

        foreach (var roleName in roleNames
            ?? await userRepository.GetRoleNamesAsync(user.Id, cancellationToken))
        {
            identity.AddClaim(new Claim(RoleClaimType, roleName));
        }

        foreach (var claim in extraClaims)
        {
            identity.AddClaim(claim);
        }

        return new ClaimsPrincipal(identity);
    }

    // "新设备"的判据刻意取保守：IP 与上次登录不同，**并且**没有一个同浏览器标识的有效会话。
    // 只看其中一项都太吵——退出后同一台电脑再登录没有会话可比，换个 Wi-Fi 同一个浏览器 IP 就变。
    // 第一次登录（没有上次 IP）与模拟登录（不是本人）都不提醒。
    private async Task<bool> IsNewDeviceAsync(
        Guid userId,
        string? previousIp,
        string? impersonatorName,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        if (impersonatorName is not null || previousIp is null || previousIp == clientInfo.IpAddress)
            return false;

        return !await asyncExecuter.AnyAsync(
            (await userSessionRepository.GetQueryableAsync(cancellationToken))
                .Where(s => s.UserId == userId && s.UserAgent == clientInfo.UserAgent)
                .Where(UserSession.ActiveAt(clock.Now, idleTimeout)),
            cancellationToken);
    }

    // 模拟登录的会话记下发起人，名称缺席时退回发起人 Id——设备列表里至少要看得出"这不是本人登录的"。
    private static string? ReadImpersonatorName(List<Claim> claims)
    {
#if (Impersonation)
        if (claims.FirstOrDefault(c => c.Type == ImpersonationClaimTypes.ImpersonatorUserId) is not { } impersonator)
            return null;

        return claims.FirstOrDefault(c => c.Type == ImpersonationClaimTypes.ImpersonatorName)?.Value ?? impersonator.Value;
#else
        return null;
#endif
    }

    /// <summary>账号锁定时的登录错误。临时锁定（登录失败触发）告诉用户还要等多久；管理员锁定只说联系管理员。</summary>
    /// <remarks>
    /// 不带锁定截止的时刻本身：服务端的时刻要按用户时区换算才有意义，"还有几分钟"则不需要。
    /// </remarks>
    public static BusinessException LockedOut(User user, DateTime now)
    {
        if (user.IsTemporarilyLockedOut(now))
        {
            var minutes = (int)Math.Ceiling((user.Lockout.End!.Value - now).TotalMinutes);
            return new BusinessException(AuthErrorCodes.UserTemporarilyLockedOut,
                    $"Too many failed sign-in attempts. Try again in {minutes} minute(s).")
                .WithData("Minutes", minutes);
        }

        return new BusinessException(AuthErrorCodes.UserLockedOut, "This account is locked. Contact your administrator.");
    }

    internal static void EnsureAllowed(User user, DateTime now)
    {
        var accessStatus = user.GetAccessStatus(now);
        switch (accessStatus)
        {
            case UserAccessStatus.Allowed:
                return;
            case UserAccessStatus.Disabled:
                throw new BusinessException(AuthErrorCodes.UserDisabled, "This account is disabled. Contact your administrator.");
            case UserAccessStatus.LockedOut:
                throw LockedOut(user, now);
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(accessStatus),
                    accessStatus,
                    "Unsupported user access status.");
        }
    }
}
