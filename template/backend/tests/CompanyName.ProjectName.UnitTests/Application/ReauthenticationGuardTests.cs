#if (LocalIdentity)
using System.Globalization;
using System.Security.Claims;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Domain.Auth.Errors;
using CompanyName.ProjectName.Domain.Users.Entities;
using Leistd.ExceptionHandling;
using Leistd.Security.Claims;
using Leistd.Security.Users;
using Leistd.Timing;

namespace CompanyName.ProjectName.UnitTests.Application;

/// <summary>"刚认证过"的判据：只认会话的认证时间，模拟会话不算；不新鲜时按账号有无本地口令给出不同的要求。</summary>
/// <remarks>
/// 只覆盖不出示口令的判定表：出示口令时的比对、失败计数与锁定走真实数据库，由集成测试覆盖。
/// 这里用到的依赖只有会话声明与时钟，其余依赖传空，走到它们即说明判定表漏了分支。
/// </remarks>
public class ReauthenticationGuardTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_session_authenticated_within_the_window_needs_nothing_more()
    {
        var user = LocalUser();
        var guard = Guard(user, authenticatedAt: Now - ReauthenticationGuard.RecentAuthenticationWindow);

        await guard.EnsureRecentlyAuthenticatedAsync(user, currentPassword: null, OperationRecordActions.AuthTwoFactorEnabled);
    }

    [Fact]
    public async Task An_older_session_of_a_password_account_must_present_the_password()
    {
        var user = LocalUser();
        var guard = Guard(user, authenticatedAt: Now - ReauthenticationGuard.RecentAuthenticationWindow - TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            guard.EnsureRecentlyAuthenticatedAsync(user, currentPassword: null, OperationRecordActions.AuthTwoFactorEnabled));

        Assert.Equal(AuthErrorCodes.ReauthenticationRequired, error.Code);
    }

    // 没有本地口令可出示：唯一的办法是重新走一遍外部登录，拿到新的认证时间
    [Fact]
    public async Task An_older_session_of_an_account_without_a_password_must_sign_in_again()
    {
        var user = new User("external_only", "external_only@example.test");
        var guard = Guard(user, authenticatedAt: Now - TimeSpan.FromHours(1));

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            guard.EnsureRecentlyAuthenticatedAsync(user, currentPassword: null, OperationRecordActions.AuthTwoFactorEnabled));

        Assert.Equal(AuthErrorCodes.SignInAgainRequired, error.Code);
    }

    // 模拟会话的认证时间是发起人的，不能替被模拟的账号证明"本人刚认证过"
    [Fact]
    public async Task An_impersonated_session_never_counts_as_recent()
    {
        var user = LocalUser();
        var guard = Guard(user, authenticatedAt: Now, impersonated: true);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            guard.EnsureRecentlyAuthenticatedAsync(user, currentPassword: null, OperationRecordActions.AuthTwoFactorEnabled));

        Assert.Equal(AuthErrorCodes.ReauthenticationRequired, error.Code);
    }

    [Fact]
    public async Task A_session_without_an_authentication_time_is_not_recent()
    {
        var user = LocalUser();
        var guard = Guard(user, authenticatedAt: null);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            guard.EnsureRecentlyAuthenticatedAsync(user, currentPassword: null, OperationRecordActions.AuthTwoFactorEnabled));

        Assert.Equal(AuthErrorCodes.ReauthenticationRequired, error.Code);
    }

    [Fact]
    public async Task Only_the_signed_in_user_can_be_checked()
    {
        var signedIn = LocalUser();
        var guard = Guard(signedIn, authenticatedAt: Now);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.EnsureRecentlyAuthenticatedAsync(LocalUser(), currentPassword: null, OperationRecordActions.UserTwoFactorReset));
    }

    private static User LocalUser() => new($"user_{Guid.NewGuid():N}"[..20], "user@example.test", passwordHash: "hash");

    private static ReauthenticationGuard Guard(User signedIn, DateTime? authenticatedAt, bool impersonated = false)
    {
        var claims = new List<Claim>();
        if (authenticatedAt is { } at)
        {
            claims.Add(new Claim("auth_time", new DateTimeOffset(at).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        }

        if (impersonated)
        {
            claims.Add(new Claim(CustomClaimTypes.ImpersonatorUserId, Guid.NewGuid().ToString()));
        }

        return new ReauthenticationGuard(
            accessFailureCounter: null!,
            operationRecorder: null!,
            userDomainService: null!,
            new ClaimsCurrentUser(signedIn.Id, claims),
            new FixedClock(Now));
    }

    private sealed class ClaimsCurrentUser(Guid id, IReadOnlyList<Claim> claims) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public string? SubjectId => Id.ToString();
        public Guid? Id { get; } = id;
        public Guid? TenantId => null;
        public string? Username => null;
        public string? Name => null;
        public string? Email => null;
        public Claim? FindClaim(string claimType) => claims.FirstOrDefault(claim => claim.Type == claimType);
        public IReadOnlyList<Claim> FindClaims(string claimType) => claims.Where(claim => claim.Type == claimType).ToList();
        public bool IsInRole(string role) => false;
    }

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime Now { get; } = now;
        public DateTime Normalize(DateTime dateTime) => dateTime;
    }
}
#endif
