#if (LocalIdentity)
using System.Globalization;
using System.Security.Claims;
using System.Text;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Domain.Auth.Errors;
using CompanyName.ProjectName.Domain.Shared.Security.PasswordHash;
using CompanyName.ProjectName.Domain.Users.DomainServices;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.Errors;
using Leistd.ExceptionHandling;
using Leistd.Security.Claims;
using Leistd.Security.OneTimeCodes;
using Leistd.Security.Users;
using Leistd.Timing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CompanyName.ProjectName.UnitTests.Application;

/// <summary>确认身份的判定表：出示了凭证只按凭证判定，没出示时只认会话的认证时间；本账号不具备的方式不计数。</summary>
/// <remarks>
/// 只覆盖对已读出的操作者做的判定（<c>VerifyAsync</c>）。独立工作单元的提交、并发与跨入口重放走真实数据库，由集成测试覆盖。
/// 这里用不到的依赖传空，走到它们即说明判定表漏了分支；计数用记录调用次数的假实现，断言"不计数"。
/// </remarks>
public class ReauthenticationGuardTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly byte[] Secret = Encoding.ASCII.GetBytes("12345678901234567890");
    private static readonly IDataProtectionProvider DataProtection = new EphemeralDataProtectionProvider();

    [Fact]
    public async Task A_session_authenticated_within_the_window_needs_nothing_more()
    {
        var user = LocalUser();
        var guard = Guard(user, authenticatedAt: Now - ReauthenticationGuard.RecentAuthenticationWindow);

        Assert.Equal(ReauthenticationMethod.RecentSignIn, await guard.VerifyAsync(user, ReauthenticationProof.None, Action));
    }

    [Fact]
    public async Task An_older_session_of_a_password_account_must_confirm_it_is_them()
    {
        var user = LocalUser();
        var guard = Guard(user, authenticatedAt: Now - ReauthenticationGuard.RecentAuthenticationWindow - TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<BusinessException>(() => guard.VerifyAsync(user, ReauthenticationProof.None, Action));

        Assert.Equal(AuthErrorCodes.ReauthenticationRequired, error.Code);
    }

    // 没有口令但开了两步验证：手里有验证码可出示，不必重新走外部登录
    [Fact]
    public async Task An_older_session_without_a_password_but_with_two_factor_is_asked_for_a_code()
    {
        var user = WithTwoFactor(new User("external_tfa", "external_tfa@example.test"));
        var guard = Guard(user, authenticatedAt: Now - TimeSpan.FromHours(1));

        var error = await Assert.ThrowsAsync<BusinessException>(() => guard.VerifyAsync(user, ReauthenticationProof.None, Action));

        Assert.Equal(AuthErrorCodes.ReauthenticationRequired, error.Code);
    }

    // 既没有口令也没开两步验证：拿不出任何凭证，只能重新走一遍外部登录拿到新的认证时间
    [Fact]
    public async Task An_older_session_without_a_password_or_two_factor_must_sign_in_again()
    {
        var user = new User("external_only", "external_only@example.test");
        var guard = Guard(user, authenticatedAt: Now - TimeSpan.FromHours(1));

        var error = await Assert.ThrowsAsync<BusinessException>(() => guard.VerifyAsync(user, ReauthenticationProof.None, Action));

        Assert.Equal(AuthErrorCodes.SignInAgainRequired, error.Code);
    }

    // 模拟会话的认证时间是发起人的，不能替被模拟的账号证明"本人刚认证过"
    [Fact]
    public async Task An_impersonated_session_never_counts_as_recent()
    {
        var user = LocalUser();
        var guard = Guard(user, authenticatedAt: Now, impersonated: true);

        var error = await Assert.ThrowsAsync<BusinessException>(() => guard.VerifyAsync(user, ReauthenticationProof.None, Action));

        Assert.Equal(AuthErrorCodes.ReauthenticationRequired, error.Code);
    }

    [Fact]
    public async Task A_session_without_an_authentication_time_is_not_recent()
    {
        var user = LocalUser();
        var guard = Guard(user, authenticatedAt: null);

        var error = await Assert.ThrowsAsync<BusinessException>(() => guard.VerifyAsync(user, ReauthenticationProof.None, Action));

        Assert.Equal(AuthErrorCodes.ReauthenticationRequired, error.Code);
    }

    [Fact]
    public async Task Only_the_signed_in_user_can_be_checked()
    {
        var signedIn = LocalUser();
        var guard = Guard(signedIn, authenticatedAt: Now);

        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.VerifyAsync(LocalUser(), ReauthenticationProof.None, Action));
    }

    [Fact]
    public async Task The_correct_password_passes_an_older_session()
    {
        var user = LocalUser();
        var guard = Guard(user, authenticatedAt: Now - TimeSpan.FromHours(1));

        Assert.Equal(
            ReauthenticationMethod.Password,
            await guard.VerifyAsync(user, new ReauthenticationProof(CorrectPassword, null), Action));
    }

    // 显式出示的凭证只按它判定：错了不回落到"会话还新"，并计入失败
    [Fact]
    public async Task A_wrong_password_is_rejected_and_counted_even_in_a_fresh_session()
    {
        var user = LocalUser();
        var counter = new CountingFailureCounter();
        var guard = Guard(user, authenticatedAt: Now, counter: counter);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            guard.VerifyAsync(user, new ReauthenticationProof("wrong", null), Action));

        Assert.Equal(SecurityErrorCodes.CurrentPasswordIncorrect, error.Code);
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public async Task The_correct_code_passes_and_its_step_cannot_be_used_again()
    {
        var user = WithTwoFactor(LocalUser());
        var guard = Guard(user, authenticatedAt: Now - TimeSpan.FromHours(1), counter: new CountingFailureCounter());
        var code = CurrentCode();

        Assert.Equal(ReauthenticationMethod.TwoFactorCode, await guard.VerifyAsync(user, new ReauthenticationProof(null, code), Action));
        Assert.Equal(Totp.TimeStepAt(new DateTimeOffset(Now)), user.TwoFactor!.LastUsedStep);

        var replay = await Assert.ThrowsAsync<BusinessException>(() =>
            guard.VerifyAsync(user, new ReauthenticationProof(null, code), Action));
        Assert.Equal(AuthErrorCodes.TwoFactorCodeInvalid, replay.Code);
    }

    [Fact]
    public async Task A_wrong_code_is_rejected_and_counted_even_in_a_fresh_session()
    {
        var user = WithTwoFactor(LocalUser());
        var counter = new CountingFailureCounter();
        var guard = Guard(user, authenticatedAt: Now, counter: counter);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            guard.VerifyAsync(user, new ReauthenticationProof(null, WrongCode()), Action));

        Assert.Equal(AuthErrorCodes.TwoFactorCodeInvalid, error.Code);
        Assert.Equal(1, counter.Calls);
    }

    // 没有这种凭据就没什么可猜的：不计数，否则别人能借此把只用外部登录的账号锁住（G2 的成因）
    [Fact]
    public async Task A_password_for_an_account_without_one_is_unavailable_and_not_counted()
    {
        var user = WithTwoFactor(new User("external_tfa", "external_tfa@example.test"));
        var counter = new CountingFailureCounter();
        var guard = Guard(user, authenticatedAt: Now - TimeSpan.FromHours(1), counter: counter);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            guard.VerifyAsync(user, new ReauthenticationProof("anything", null), Action));

        Assert.Equal(AuthErrorCodes.ReauthenticationMethodUnavailable, error.Code);
        Assert.Equal(0, counter.Calls);
    }

    [Fact]
    public async Task A_code_for_an_account_without_two_factor_is_unavailable_and_not_counted()
    {
        var user = LocalUser();
        var counter = new CountingFailureCounter();
        var guard = Guard(user, authenticatedAt: Now, counter: counter);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            guard.VerifyAsync(user, new ReauthenticationProof(null, CurrentCode()), Action));

        Assert.Equal(AuthErrorCodes.ReauthenticationMethodUnavailable, error.Code);
        Assert.Equal(0, counter.Calls);
    }

    [Fact]
    public async Task Both_proofs_at_once_are_refused()
    {
        var user = WithTwoFactor(LocalUser());
        var guard = Guard(user, authenticatedAt: Now);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            guard.VerifyAsync(user, new ReauthenticationProof(CorrectPassword, CurrentCode()), Action));
        Assert.Single(ReauthenticationProof.ValidateOneAtATime(CorrectPassword, CurrentCode(), "twoFactorCode"));
        Assert.Empty(ReauthenticationProof.ValidateOneAtATime(CorrectPassword, " ", "twoFactorCode"));
    }

    // 锁定期内先拒绝：不比对、不计数，被盗会话不能借再认证继续猜
    [Fact]
    public async Task A_locked_out_account_is_refused_before_any_proof_is_checked()
    {
        var user = WithTwoFactor(LocalUser());
        user.Lock(Now.AddMinutes(10));
        var counter = new CountingFailureCounter();
        var guard = Guard(user, authenticatedAt: Now, counter: counter);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            guard.VerifyAsync(user, new ReauthenticationProof(null, CurrentCode()), Action));

        Assert.Equal(AuthErrorCodes.UserTemporarilyLockedOut, error.Code);
        Assert.Equal(0, counter.Calls);
        Assert.NotEqual(Totp.TimeStepAt(new DateTimeOffset(Now)), user.TwoFactor!.LastUsedStep);
    }

    private const string Action = OperationRecordActions.AuthEmailChanged;
    private const string CorrectPassword = "correct";

    private static User LocalUser() => new($"user_{Guid.NewGuid():N}"[..20], "user@example.test", passwordHash: "hash:" + CorrectPassword);

    private static User WithTwoFactor(User user)
    {
        var protectedSecret = new TwoFactorDomainService(DataProtection).ProtectSecret(Secret);
        user.EnableTwoFactor(protectedSecret, ["recovery-hash"], Totp.TimeStepAt(new DateTimeOffset(Now)) - 10);
        return user;
    }

    private static string CurrentCode() => Totp.ComputeCode(Secret, Totp.TimeStepAt(new DateTimeOffset(Now)));

    private static string WrongCode()
    {
        var code = CurrentCode();
        return code == "000000" ? "111111" : "000000";
    }

    private static ReauthenticationGuard Guard(
        User signedIn,
        DateTime? authenticatedAt,
        bool impersonated = false,
        IAccessFailureCounter? counter = null)
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
            accessFailureCounter: counter!,
            operationRecorder: null!,
            new UserDomainService(null!, null!, null!, new PrefixPasswordHasher(), NullLogger<UserDomainService>.Instance),
            new TwoFactorDomainService(DataProtection),
            userRepository: null!,
            unitOfWorkManager: null!,
            distributedLock: null!,
            new ClaimsCurrentUser(signedIn.Id, claims),
            new FixedClock(Now));
    }

    /// <summary>失败计数的假实现：只记调用次数，返回"行已不存在"让守卫按原码拒绝、不去留审计。</summary>
    private sealed class CountingFailureCounter : IAccessFailureCounter
    {
        public int Calls { get; private set; }

        public Task<AccessFailureOutcome> CountAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new AccessFailureOutcome(null, LockoutTriggered: false));
        }

        public Task RecordLockoutAsync(User user, string authorizationBasis, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Lockout is not expected in these cases.");
    }

    /// <summary>口令哈希的假实现：哈希即 <c>hash:</c> 前缀加原文，判定不涉及真实算法。</summary>
    private sealed class PrefixPasswordHasher : IPasswordHasher
    {
        public string HashPassword(string password) => "hash:" + password;

        public PasswordVerificationStatus VerifyPassword(string hashedPassword, string providedPassword) =>
            hashedPassword == HashPassword(providedPassword) ? PasswordVerificationStatus.Succeeded : PasswordVerificationStatus.Failed;
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
