#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Domain.Users.Constants;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.Security.OneTimeCodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>
/// 确认身份时出示身份验证器上的验证码：同一步只认一次（跨入口、跨登录第二步），通过即落库、业务失败也不退回；
/// 管理员出示的是自己的码，确认阶段只持自己的凭据锁，与目标的锁先后而不嵌套。
/// </summary>
/// <remarks>
/// 时钟换成手动推进的：会话要开过"刚认证过"的窗口才会要求凭证，同一步的码只认一次，用例要在不同的步上各取一个码。
/// 判定表本身（凭证互斥、方式不具备不计数、显式坏凭证不回落）由 <c>ReauthenticationGuardTests</c> 单测覆盖，这里验证落库、并发与接线。
/// </remarks>
public sealed class TwoFactorReauthenticationTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Password = "TfaReauth!Passw0rd";

    /// <summary>超出"刚认证过"的窗口。</summary>
    private static readonly TimeSpan StaleSessionAge = TimeSpan.FromMinutes(16);

    // 确认身份用掉的步换一个请求（新的作用域，读库里的值）再用就被拒：另一个敏感入口不行，登录第二步也不行
    [Fact]
    public async Task A_code_that_confirmed_a_change_cannot_be_replayed_anywhere()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var (username, userId) = await CreateUserAsync(host, "tfa_reauth_replay");
        var (session, secret) = await SignInWithTwoFactorAsync(host, username, clock);
        using var __ = session;
        clock.Advance(StaleSessionAge);
        Step(clock);
        var code = Code(secret, clock);

        using (var changed = await ChangeEmailAsync(session.Client, username, "first", twoFactorCode: code))
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(Totp.TimeStepAt(clock.GetUtcNow()), await LastUsedStepAsync(host, userId));

        using (var replay = await ChangeEmailAsync(session.Client, username, "second", twoFactorCode: code))
            Assert.Equal("Auth:TwoFactorCodeInvalid", await ErrorCodeAsync(replay));
        var token = await PasswordStepAsync(host, username);
        Assert.Equal("Auth:TwoFactorCodeInvalid", await SecondStepErrorAsync(host, new { Token = token, Code = code }));
    }

    // 反方向：登录第二步刚用过的码，紧接着确认身份同样被拒，要等下一个码
    [Fact]
    public async Task A_code_just_used_to_sign_in_cannot_confirm_a_change()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var (username, _) = await CreateUserAsync(host, "tfa_reauth_after_login");
        var (_, secret) = await SignInWithTwoFactorAsync(host, username, clock);
        Step(clock);
        var code = Code(secret, clock);
        var token = await PasswordStepAsync(host, username);
        using var signedIn = await SecondStepAsync(host, new { Token = token, Code = code });

        // 会话刚认证过，但显式出示的码只按码判定：同一步已被登录用掉
        using var replay = await ChangeEmailAsync(signedIn, username, "after", twoFactorCode: code);

        Assert.Equal("Auth:TwoFactorCodeInvalid", await ErrorCodeAsync(replay));
    }

    // 验证码通过即用掉并独立提交：随后的业务失败（邮箱已被占用）不退回，这个码再也不能用
    [Fact]
    public async Task A_code_stays_used_when_the_change_it_confirmed_fails()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var (taken, _) = await CreateUserAsync(host, "tfa_reauth_taken");
        var (username, userId) = await CreateUserAsync(host, "tfa_reauth_rollback");
        var (session, secret) = await SignInWithTwoFactorAsync(host, username, clock);
        using var __ = session;
        clock.Advance(StaleSessionAge);
        Step(clock);
        var code = Code(secret, clock);

        using (var conflict = await session.Client.PutAsJsonAsync("/api/v1/auth/me",
                   new { Username = username, Email = $"{taken}@example.test", TwoFactorCode = code }))
            Assert.Equal("User:EmailTaken", await ErrorCodeAsync(conflict));
        Assert.Equal(Totp.TimeStepAt(clock.GetUtcNow()), await LastUsedStepAsync(host, userId));

        using var retry = await ChangeEmailAsync(session.Client, username, "retry", twoFactorCode: code);
        Assert.Equal("Auth:TwoFactorCodeInvalid", await ErrorCodeAsync(retry));
        Assert.Equal($"{username}@example.test", await EmailOfAsync(session.Client));
    }

    // 显式出示的坏凭证不回落到"会话还新"：刚登录的会话带错口令照样被拒并计数，邮箱不变
    [Fact]
    public async Task An_explicit_wrong_proof_is_rejected_even_from_a_fresh_session()
    {
        var (host, _) = CreateHost();
        using var _host = host;
        var (username, userId) = await CreateUserAsync(host, "tfa_reauth_fresh_bad");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);

        using var wrong = await session.Client.PutAsJsonAsync("/api/v1/auth/me",
            new { Username = username, Email = $"new-{username}@example.test", CurrentPassword = Password + "x" });

        Assert.Equal("Security:CurrentPasswordIncorrect", await ErrorCodeAsync(wrong));
        Assert.Equal(1, await AccessFailedCountAsync(host, userId));
        Assert.Equal($"{username}@example.test", await EmailOfAsync(session.Client));
    }

    // 一次只出示一种凭证：都给了是字段错误，不猜用哪个，也不计数
    [Fact]
    public async Task Presenting_both_proofs_is_a_field_error()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var (username, userId) = await CreateUserAsync(host, "tfa_reauth_both");
        var (session, secret) = await SignInWithTwoFactorAsync(host, username, clock);
        using var __ = session;
        Step(clock);

        using var both = await ChangeEmailAsync(session.Client, username, "both", currentPassword: Password, twoFactorCode: Code(secret, clock));

        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        using var body = JsonDocument.Parse(await both.Content.ReadAsStringAsync());
        Assert.Contains(body.RootElement.GetProperty("errors").EnumerateArray(), error => error.GetProperty("field").GetString() == "twoFactorCode");
        Assert.Equal(0, await AccessFailedCountAsync(host, userId));
    }

    // 没开两步验证却给验证码：这种方式不具备，不是猜错，不计入失败
    [Fact]
    public async Task A_code_from_an_account_without_two_factor_is_unavailable_and_not_counted()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var (username, userId) = await CreateUserAsync(host, "tfa_reauth_unavailable");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        clock.Advance(StaleSessionAge);

        using var unavailable = await ChangeEmailAsync(session.Client, username, "unavailable", twoFactorCode: "123456");

        Assert.Equal("Auth:ReauthenticationMethodUnavailable", await ErrorCodeAsync(unavailable));
        Assert.Equal(0, await AccessFailedCountAsync(host, userId));
    }

    // 管理员出示的是自己的码：校验与用掉的步都落在管理员自己的账号上，目标账号的码不算数
    [Fact]
    public async Task An_administrator_confirms_with_their_own_code_and_spends_their_own_step()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var (adminName, adminId) = await CreateUserAsync(host, "tfa_reauth_admin", isAdmin: true);
        var (targetName, targetId) = await CreateUserAsync(host, "tfa_reauth_target");
        var (admin, adminSecret) = await SignInWithTwoFactorAsync(host, adminName, clock);
        using var __ = admin;
        var (_, targetSecret) = await SignInWithTwoFactorAsync(host, targetName, clock);
        var targetStep = await LastUsedStepAsync(host, targetId);
        clock.Advance(StaleSessionAge);
        Step(clock);

        using (var withTargetCode = await admin.Client.PostAsJsonAsync($"/api/v1/users/{targetId}/reset-password",
                   new { Password = "TfaReauth!Reset1", TwoFactorCode = Code(targetSecret, clock) }))
            Assert.Equal("Auth:TwoFactorCodeInvalid", await ErrorCodeAsync(withTargetCode));

        var code = Code(adminSecret, clock);
        using (var reset = await admin.Client.PostAsJsonAsync($"/api/v1/users/{targetId}/reset-password",
                   new { Password = "TfaReauth!Reset1", TwoFactorCode = code }))
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(Totp.TimeStepAt(clock.GetUtcNow()), await LastUsedStepAsync(host, adminId));
        Assert.Equal(targetStep, await LastUsedStepAsync(host, targetId));

        using var replay = await admin.Client.PostAsJsonAsync($"/api/v1/users/{targetId}/reset-two-factor", new { TwoFactorCode = code });
        Assert.Equal("Auth:TwoFactorCodeInvalid", await ErrorCodeAsync(replay));
        Assert.True(await IsTwoFactorEnabledAsync(host, targetId));
    }

    // 管理员经用户管理重置自己的口令是允许的：确认身份与重置先后取同一把锁，不嵌套，当前会话保留
    [Fact]
    public async Task Resetting_your_own_password_through_user_management_still_works()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var (adminName, adminId) = await CreateUserAsync(host, "tfa_reauth_self_reset", isAdmin: true);
        var (admin, secret) = await SignInWithTwoFactorAsync(host, adminName, clock);
        using var __ = admin;
        clock.Advance(StaleSessionAge);
        Step(clock);

        using var reset = await admin.Client.PostAsJsonAsync($"/api/v1/users/{adminId}/reset-password",
            new { Password = "TfaReauth!Self1", TwoFactorCode = Code(secret, clock) });

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    // 换绑时现有验证器上的码同时确认身份，只校验一次：开久了的会话只带这个码就能开始换绑
    [Fact]
    public async Task Replacing_the_authenticator_checks_the_current_code_only_once()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var (username, _) = await CreateUserAsync(host, "tfa_reauth_replace");
        var (session, secret) = await SignInWithTwoFactorAsync(host, username, clock);
        using var __ = session;
        clock.Advance(StaleSessionAge);
        Step(clock);

        using var replace = await session.Client.PostAsJsonAsync(SetupUrl, new { Code = Code(secret, clock) });

        Assert.True(replace.StatusCode == HttpStatusCode.OK, await replace.Content.ReadAsStringAsync());
    }

    // 登录第二步与确认身份、两个不同的敏感入口同时拿同一个码：凭据锁把它们排成先后，至多一个成功
    [Fact]
    public async Task Racing_uses_of_one_code_succeed_at_most_once()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var (username, _) = await CreateUserAsync(host, "tfa_reauth_race");
        var (session, secret) = await SignInWithTwoFactorAsync(host, username, clock);
        using var __ = session;
        clock.Advance(StaleSessionAge);
        Step(clock);
        var code = Code(secret, clock);
        var token = await PasswordStepAsync(host, username);

        using var secondStepClient = ProjectWebApplicationFactory.CreateProjectClient(host);
        var responses = await Task.WhenAll(
            secondStepClient.PostAsJsonAsync("/api/v1/auth/two-factor", new { Token = token, Code = code }),
            ChangeEmailAsync(session.Client, username, "race", twoFactorCode: code),
            session.Client.PostAsJsonAsync(SetupUrl, new { Code = code }));
        try
        {
            Assert.Equal(1, responses.Count(response => response.IsSuccessStatusCode));
            foreach (var rejected in responses.Where(response => !response.IsSuccessStatusCode))
                Assert.Equal("Auth:TwoFactorCodeInvalid", await ErrorCodeAsync(rejected));
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }

    /// <summary>
    /// 管理员确认身份时持着自己的凭据锁：确认停在中途（这里停在错码的失败计数上）时，同一个人的登录第二步只能等它结束。
    /// 少了这把锁，两边会读到同一个"已用步"、各自放行同一个码。
    /// </summary>
    [Fact]
    public async Task An_administrators_confirmation_holds_their_own_credential_lock()
    {
        var gate = new InterleavingGate();
        var waits = new LockWaitProbe();
        var (host, clock) = CreateHost(services =>
        {
            InterleavingGate.Wrap<IAccessFailureCounter>(services, inner => new CountingGate(gate, inner));
            waits.Install(services);
        });
        using var _ = host;
        var (adminName, adminId) = await CreateUserAsync(host, "tfa_reauth_lock", isAdmin: true);
        var (_, targetId) = await CreateUserAsync(host, "tfa_reauth_lock_target");
        var (admin, secret) = await SignInWithTwoFactorAsync(host, adminName, clock);
        using var __ = admin;
        clock.Advance(StaleSessionAge);
        Step(clock);
        var code = Code(secret, clock);
        var token = await PasswordStepAsync(host, adminName);

        gate.Arm();
        var confirming = admin.Client.PostAsJsonAsync($"/api/v1/users/{targetId}/reset-password",
            new { Password = "TfaReauth!Lock1", TwoFactorCode = WrongCode(code) });
        await gate.Entered;
        waits.Arm(UserCredentialLock.Key(adminId));
        using var secondStepClient = ProjectWebApplicationFactory.CreateProjectClient(host);
        var signingIn = secondStepClient.PostAsJsonAsync("/api/v1/auth/two-factor", new { Token = token, Code = code });
        await waits.AssertWaitingAsync(signingIn);
        gate.Release();

        using (var rejected = await confirming)
            Assert.Equal("Auth:TwoFactorCodeInvalid", await ErrorCodeAsync(rejected));
        using (var signedIn = await signingIn)
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
    }

    /// <summary>
    /// 两位管理员互相改对方邮箱：确认阶段只持自己的锁、释放后才取对方的锁，交错时都能完成。
    /// 把确认挪进目标锁内（目标锁 → 自己的锁）会让两边各持一把、互等对方。
    /// </summary>
    [Fact]
    public async Task Administrators_acting_on_each_other_never_hold_both_locks()
    {
        var gate = new InterleavingGate();
        Guid gatedAdminId = default;
        var (host, clock) = CreateHost(services => gate.GateLock(services, key => key == UserCredentialLock.Key(gatedAdminId)));
        using var _ = host;
        var (firstName, firstId) = await CreateUserAsync(host, "tfa_reauth_cross_a", isAdmin: true);
        var (secondName, secondId) = await CreateUserAsync(host, "tfa_reauth_cross_b", isAdmin: true);
        gatedAdminId = firstId;
        var (first, firstSecret) = await SignInWithTwoFactorAsync(host, firstName, clock);
        using var __ = first;
        var (second, secondSecret) = await SignInWithTwoFactorAsync(host, secondName, clock);
        using var ___ = second;
        clock.Advance(StaleSessionAge);
        Step(clock);

        // 第一位管理员改第二位：停在第一次去取自己那把锁之前
        gate.Arm();
        var firstUpdate = first.Client.PutAsJsonAsync($"/api/v1/users/{secondId}",
            new { Email = $"cross-{secondName}@example.test", IsEmailVerified = false, TwoFactorCode = Code(firstSecret, clock) });
        await gate.Entered;
        using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        using (var secondUpdate = await second.Client.PutAsJsonAsync($"/api/v1/users/{firstId}",
                   new { Email = $"cross-{firstName}@example.test", IsEmailVerified = false, TwoFactorCode = Code(secondSecret, clock) },
                   cancel.Token))
            Assert.Equal(HttpStatusCode.OK, secondUpdate.StatusCode);
        gate.Release();

        using var firstResponse = await firstUpdate;
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
    }

    private const string SetupUrl = "/api/v1/auth/me/two-factor/setup";

    private sealed class CountingGate(InterleavingGate gate, IAccessFailureCounter inner) : IAccessFailureCounter
    {
        public async Task<AccessFailureOutcome> CountAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            await gate.PassAsync();
            return await inner.CountAsync(userId, cancellationToken);
        }

        public Task RecordLockoutAsync(User user, string authorizationBasis, CancellationToken cancellationToken = default) =>
            inner.RecordLockoutAsync(user, authorizationBasis, cancellationToken);
    }

    private (WebApplicationFactory<Program> Host, FakeTimeProvider Clock) CreateHost(Action<IServiceCollection>? configure = null)
    {
        // 从此刻起步：缓存条目按真实时钟回收，假时钟落在过去会让刚写入的挑战被缓存当成已过期
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            configure?.Invoke(services);
        }));
        return (host, clock);
    }

    /// <summary>登录并启用两步验证；返回启用它的那个会话（其他会话随启用退出）与密钥。</summary>
    private static async Task<(AuthenticatedSession Session, string Secret)> SignInWithTwoFactorAsync(
        WebApplicationFactory<Program> host, string username, FakeTimeProvider clock)
    {
        var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        using var begin = await session.Client.PostAsJsonAsync(SetupUrl, new { });
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        using var setup = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
        var secret = setup.RootElement.GetProperty("secret").GetString()!;
        Step(clock);
        using var enable = await session.Client.PostAsJsonAsync("/api/v1/auth/me/two-factor/enable",
            new { SetupId = setup.RootElement.GetProperty("setupId").GetGuid(), Code = Code(secret, clock) });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        return (session, secret);
    }

    private static Task<HttpResponseMessage> ChangeEmailAsync(
        HttpClient client, string username, string prefix, string? currentPassword = null, string? twoFactorCode = null) =>
        client.PutAsJsonAsync("/api/v1/auth/me", new
        {
            Username = username,
            Email = $"{prefix}-{username}@example.test",
            CurrentPassword = currentPassword,
            TwoFactorCode = twoFactorCode
        });

    private static async Task<string> PasswordStepAsync(WebApplicationFactory<Program> host, string username)
    {
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/session-login", new { UsernameOrEmail = username, Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("requiresTwoFactor").GetBoolean());
        return body.RootElement.GetProperty("twoFactorToken").GetString()!;
    }

    private static async Task<HttpClient> SecondStepAsync(WebApplicationFactory<Program> host, object input)
    {
        var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/two-factor", input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ",
            response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';', 2)[0])));
        return client;
    }

    private static async Task<string?> SecondStepErrorAsync(WebApplicationFactory<Program> host, object input)
    {
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/two-factor", input);
        Assert.False(response.IsSuccessStatusCode);
        return await ErrorCodeAsync(response);
    }

    private static async Task<(string Username, Guid Id)> CreateUserAsync(
        WebApplicationFactory<Program> host, string prefix, bool isAdmin = false)
    {
        var username = $"{prefix}_{Guid.NewGuid():N}"[..30];
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var create = await admin.Client.PostAsJsonAsync("/api/v1/users", new
        {
            Username = username,
            Email = $"{username}@example.test",
            Password,
            IsActive = true
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var body = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();
        if (isAdmin)
        {
            Guid adminRoleId;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
                adminRoleId = await db.Roles.Where(role => role.Name == AdminConstant.RoleName).Select(role => role.Id).SingleAsync();
            }

            using var assign = await admin.Client.PutAsJsonAsync($"/api/v1/users/{id}/roles", new { RoleIds = new[] { adminRoleId } });
            Assert.True(assign.IsSuccessStatusCode, await assign.Content.ReadAsStringAsync());
        }

        return (username, id);
    }

    private static async Task<User> ReadUserAsync(WebApplicationFactory<Program> host, Guid userId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        return await db.Set<User>().IgnoreQueryFilters().AsNoTracking().SingleAsync(user => user.Id == userId);
    }

    private static async Task<long?> LastUsedStepAsync(WebApplicationFactory<Program> host, Guid userId) =>
        (await ReadUserAsync(host, userId)).TwoFactor?.LastUsedStep;

    private static async Task<int> AccessFailedCountAsync(WebApplicationFactory<Program> host, Guid userId) =>
        (await ReadUserAsync(host, userId)).Lockout.AccessFailedCount;

    private static async Task<bool> IsTwoFactorEnabledAsync(WebApplicationFactory<Program> host, Guid userId) =>
        (await ReadUserAsync(host, userId)).TwoFactorEnabled;

    private static async Task<string?> EmailOfAsync(HttpClient client)
    {
        using var me = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        return me.RootElement.GetProperty("email").GetString();
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static string Code(string secret, FakeTimeProvider clock) =>
        Totp.ComputeCode(Totp.ParseSecret(secret)!, Totp.TimeStepAt(clock.GetUtcNow()));

    private static string WrongCode(string code) => code == "000000" ? "111111" : "000000";

    /// <summary>推进一个验证码步长。</summary>
    private static void Step(FakeTimeProvider clock) => clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
}
#endif
