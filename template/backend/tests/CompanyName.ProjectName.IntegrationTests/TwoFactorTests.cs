#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Application.Settings.Provider;
using Leistd.Security.OneTimeCodes;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>两步验证：设置与启用、登录第二步、恢复码、强制启用与管理员重置。</summary>
/// <remarks>
/// 时钟换成手动推进的：同一步的验证码只认一次（防重放），用例要在不同的步上各取一个码，
/// 靠真实时钟就得等 30 秒或碰运气。
/// </remarks>
public sealed class TwoFactorTests(ProjectWebApplicationFactory factory) : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Password = "TwoFactorTests!Passw0rd";

    [Fact]
    public async Task Enabling_requires_a_code_and_signs_out_other_devices()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_enable");
        using var other = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        using var mine = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);

        var secret = await BeginSetupAsync(mine.Client);
        using (var wrong = await mine.Client.PostAsJsonAsync("/api/v1/auth/me/two-factor/enable", new { Code = "000000" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }

        var codes = await EnableAsync(mine.Client, secret, clock);

        Assert.Equal(RecoveryCodes.Count, codes.Count);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        var status = await mine.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/two-factor");
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(RecoveryCodes.Count, status.GetProperty("recoveryCodesLeft").GetInt32());
    }

    [Fact]
    public async Task Sign_in_requires_the_second_step_and_rejects_a_reused_code()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_login");
        var secret = await EnableForAsync(host, username, clock);

        Step(clock);
        var (first, token) = await PasswordStepAsync(host, username);
        Assert.False(first.Headers.Contains("Set-Cookie"));

        var code = Code(secret, clock);
        using var client = await SecondStepAsync(host, token, new { Token = token, Code = code });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);

        // 同一步的码重放：换一个新挑战也不认
        var (_, replayToken) = await PasswordStepAsync(host, username);
        Assert.Equal("Auth:TwoFactorCodeInvalid",
            await SecondStepErrorAsync(host, new { Token = replayToken, Code = code }));
    }

    /// <summary>第一步通过之后凭据变了，未完成的挑战随之作废。</summary>
    /// <remarks>
    /// 挑战绑定账号安全版本；重置口令时必须作废，撤销已有会话无法替代这一检查。
    /// </remarks>
    [Fact]
    public async Task A_password_reset_after_the_first_step_voids_the_pending_challenge()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_stamp");
        var secret = await EnableForAsync(host, username, clock);

        Step(clock);
        var (_, token) = await PasswordStepAsync(host, username);

        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var users = await admin.Client.GetFromJsonAsync<JsonElement>($"/api/v1/users?offset=0&limit=10&keyword={username}");
        var userId = users.GetProperty("items").EnumerateArray()
            .Single(u => u.GetProperty("username").GetString() == username).GetProperty("id").GetGuid();
        using var reset = await admin.Client.PostAsJsonAsync($"/api/v1/users/{userId}/reset-password", new { Password = Password + "2" });
        Assert.True(reset.IsSuccessStatusCode);

        Assert.Equal("Auth:TwoFactorChallengeExpired",
            await SecondStepErrorAsync(host, new { Token = token, Code = Code(secret, clock) }));
    }

    /// <summary>验证码与恢复码都没给是入参校验失败：400 字段错误、不带业务码，也不消耗这次挑战。</summary>
    [Fact]
    public async Task Submitting_neither_code_is_a_field_error_and_keeps_the_challenge()
    {
        var (host, clock) = CreateHost();
        using var ownedHost = host;
        var username = await CreateUserAsync(host, "tfa_empty");
        var secret = await EnableForAsync(host, username, clock);

        Step(clock);
        var (_, token) = await PasswordStepAsync(host, username);

        using (var client = ProjectWebApplicationFactory.CreateProjectClient(host))
        using (var empty = await client.PostAsJsonAsync("/api/v1/auth/two-factor", new { Token = token, RecoveryCode = "  " }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
            using var body = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
            Assert.False(body.RootElement.TryGetProperty("code", out _));
            Assert.Contains(body.RootElement.GetProperty("errors").EnumerateArray(),
                error => error.GetProperty("field").GetString() == "code");
        }

        using var signedIn = await SecondStepAsync(host, token, new { Token = token, Code = Code(secret, clock) });
        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    /// <summary>输错不延长挑战的有效期。</summary>
    /// <remarks>
    /// 第 4 分钟输错后，第 6 分钟的正确验证码仍按原到期时刻拒绝，避免重试延长挑战。
    /// </remarks>
    [Fact]
    public async Task A_wrong_code_does_not_extend_the_challenge()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_expiry");
        var secret = await EnableForAsync(host, username, clock);

        Step(clock);
        var (_, token) = await PasswordStepAsync(host, username);

        for (var i = 0; i < 8; i++) Step(clock);   // 4 分钟
        var valid = Code(secret, clock);
        var wrong = valid[..^1] + (valid[^1] == '0' ? '1' : '0');
        Assert.Equal("Auth:TwoFactorCodeInvalid", await SecondStepErrorAsync(host, new { Token = token, Code = wrong }));

        for (var i = 0; i < 4; i++) Step(clock);   // 再过 2 分钟：签发后第 6 分钟
        Assert.Equal("Auth:TwoFactorChallengeExpired",
            await SecondStepErrorAsync(host, new { Token = token, Code = Code(secret, clock) }));
    }

    [Fact]
    public async Task Recovery_code_replaces_a_code_only_once()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_recovery");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        var secret = await BeginSetupAsync(session.Client);
        var recovery = (await EnableAsync(session.Client, secret, clock))[0];

        var (_, token) = await PasswordStepAsync(host, username);
        using var client = await SecondStepAsync(host, token, new { Token = token, RecoveryCode = recovery.ToUpperInvariant() });
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/two-factor");
        Assert.Equal(RecoveryCodes.Count - 1, status.GetProperty("recoveryCodesLeft").GetInt32());

        var (_, again) = await PasswordStepAsync(host, username);
        Assert.Equal("Auth:TwoFactorCodeInvalid",
            await SecondStepErrorAsync(host, new { Token = again, RecoveryCode = recovery }));
    }

    [Fact]
    public async Task Failed_second_step_counts_toward_lockout_across_challenges()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_lockout");
        await EnableForAsync(host, username, clock);

        string? last = null;
        for (var i = 0; i < SettingConstant.Security.DefaultLockoutMaxFailedAttempts; i++)
        {
            // 每次都换一个新挑战：单个挑战的尝试上限挡不住这种试法，得靠账号级的失败计数
            var (_, token) = await PasswordStepAsync(host, username);
            last = await SecondStepErrorAsync(host, new { Token = token, Code = "000000" });
        }

        Assert.Equal("Auth:UserTemporarilyLockedOut", last);
    }

    [Fact]
    public async Task Disabling_requires_password_and_code()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_disable");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        var secret = await BeginSetupAsync(session.Client);
        await EnableAsync(session.Client, secret, clock);
        Step(clock);

        using (var wrongPassword = await session.Client.PostAsJsonAsync("/api/v1/auth/me/two-factor/disable",
                   new { Password = Password + "x", Code = Code(secret, clock) }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrongPassword.StatusCode);
        }

        using var disable = await session.Client.PostAsJsonAsync("/api/v1/auth/me/two-factor/disable",
            new { Password, Code = Code(secret, clock) });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        using var direct = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        Assert.Equal(HttpStatusCode.OK, (await direct.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    /// <summary>
    /// 前置状态由用户实体守卫：未启用时不能重发恢复码，已启用时不能再设置或再启用；
    /// 拒绝码与状态码不变，被拒的请求不改两步验证状态。
    /// </summary>
    [Fact]
    public async Task Two_factor_state_preconditions_are_rejected_with_conflict()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_state");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);

        using (var regenerate = await session.Client.PostAsJsonAsync(
                   "/api/v1/auth/me/two-factor/recovery-codes", new { Code = "000000" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, regenerate.StatusCode);
            Assert.Equal("Auth:TwoFactorNotEnabled", await ErrorCodeAsync(regenerate));
        }

        var secret = await BeginSetupAsync(session.Client);
        await EnableAsync(session.Client, secret, clock);
        Step(clock);

        using (var setupAgain = await session.Client.PostAsync("/api/v1/auth/me/two-factor/setup", null))
        {
            Assert.Equal(HttpStatusCode.Conflict, setupAgain.StatusCode);
            Assert.Equal("Auth:TwoFactorAlreadyEnabled", await ErrorCodeAsync(setupAgain));
        }

        using (var enableAgain = await session.Client.PostAsJsonAsync(
                   "/api/v1/auth/me/two-factor/enable", new { Code = Code(secret, clock) }))
        {
            Assert.Equal(HttpStatusCode.Conflict, enableAgain.StatusCode);
            Assert.Equal("Auth:TwoFactorAlreadyEnabled", await ErrorCodeAsync(enableAgain));
        }

        // 原密钥仍在用：恢复码数没变，下一步的验证码照样能重发恢复码
        var status = await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/two-factor");
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(RecoveryCodes.Count, status.GetProperty("recoveryCodesLeft").GetInt32());
        using var regenerated = await session.Client.PostAsJsonAsync(
            "/api/v1/auth/me/two-factor/recovery-codes", new { Code = Code(secret, clock) });
        Assert.Equal(HttpStatusCode.OK, regenerated.StatusCode);
    }

    [Fact]
    public async Task Tenant_requirement_grants_a_restricted_session_until_enabled()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_required");
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        await WriteSettingAsync(admin.Client, SettingConstant.Security.RequireTwoFactor, "true");
        try
        {
            using var restricted = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);

            var me = await restricted.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
            Assert.True(me.GetProperty("twoFactorSetupRequired").GetBoolean());
            using (var blocked = await restricted.Client.GetAsync("/api/v1/auth/me/sessions"))
            {
                Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
                Assert.Equal("Auth:TwoFactorSetupRequired", await ErrorCodeAsync(blocked));
            }

            // 页面不拦：整页刷新任何地址都要能拿到前端壳，再由路由守卫带去设置页
            using (var page = await restricted.Client.GetAsync("/workspace"))
            {
                Assert.NotEqual(HttpStatusCode.Forbidden, page.StatusCode);
            }

            var secret = await BeginSetupAsync(restricted.Client);
            using var enable = await restricted.Client.PostAsJsonAsync(
                "/api/v1/auth/me/two-factor/enable", new { Code = Code(secret, clock) });
            Assert.Equal(HttpStatusCode.OK, enable.StatusCode);

            // 换发的会话不再受限；旧的受限会话随之结束
            using var reissued = ProjectWebApplicationFactory.CreateProjectClient(host);
            reissued.DefaultRequestHeaders.Add("Cookie", CookieOf(enable));
            Assert.Equal(HttpStatusCode.OK, (await reissued.GetAsync("/api/v1/auth/me/sessions")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await restricted.Client.GetAsync("/api/v1/auth/me")).StatusCode);

            Step(clock);
            using var disable = await reissued.PostAsJsonAsync("/api/v1/auth/me/two-factor/disable",
                new { Password, Code = Code(secret, clock) });
            Assert.Equal("Auth:TwoFactorRequiredByPolicy", await ErrorCodeAsync(disable));
        }
        finally
        {
            await WriteSettingAsync(admin.Client, SettingConstant.Security.RequireTwoFactor, null);
        }
    }

    /// <summary>两步验证限制在授权之前拒绝，不能被记成"授权通过之后的业务拒绝"。</summary>
    /// <remarks>
    /// 限制中间件在 <c>UseAuthorization()</c> 之前抛业务异常。补记业务拒绝的中间件紧接授权之后，
    /// 这类请求到不了它；若改放到全局异常处理器里，会把端点的权限策略当成已通过的依据写进记录。
    /// </remarks>
    [Fact]
    public async Task A_restricted_session_rejected_before_authorization_leaves_no_business_failure_record()
    {
        var (host, _) = CreateHost();
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_noaudit");
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        await WriteSettingAsync(admin.Client, SettingConstant.Security.RequireTwoFactor, "true");
        try
        {
            using var restricted = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
            var roleId = Guid.NewGuid();

            using var blocked = await restricted.Client.PutAsJsonAsync(
                $"/api/v1/permissions/grants/roles/{roleId}",
                new { expectedVersion = 0, permissionNames = Array.Empty<string>() });
            Assert.Equal("Auth:TwoFactorSetupRequired", await ErrorCodeAsync(blocked));

            Assert.Empty(await OperationRecordQueries.GetFailuresAsync(
                host, admin.Client, OperationRecordActions.PermissionGrantsReplaced, $"Role/{roleId}"));
        }
        finally
        {
            await WriteSettingAsync(admin.Client, SettingConstant.Security.RequireTwoFactor, null);
        }
    }

    [Fact]
    public async Task Admin_reset_removes_the_second_step()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_reset");
        await EnableForAsync(host, username, clock);

        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var users = await admin.Client.GetFromJsonAsync<JsonElement>($"/api/v1/users?offset=0&limit=10&keyword={username}");
        var user = users.GetProperty("items").EnumerateArray().Single(u => u.GetProperty("username").GetString() == username);
        Assert.True(user.GetProperty("isTwoFactorEnabled").GetBoolean());

        using var reset = await admin.Client.PostAsync($"/api/v1/users/{user.GetProperty("id").GetGuid()}/reset-two-factor", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        using var direct = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        Assert.Equal(HttpStatusCode.OK, (await direct.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Concurrent_regeneration_accepts_one_totp_step_and_commits_one_new_code_set()
    {
        var (host, clock) = CreateHost(); using var _ = host;
        var username = await CreateUserAsync(host, "tfa_regen_race");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        var secret = await BeginSetupAsync(session.Client); var old = await EnableAsync(session.Client, secret, clock); Step(clock);
        var code = Code(secret, clock);
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => session.Client.PostAsJsonAsync("/api/v1/auth/me/two-factor/recovery-codes", new { Code = code })));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.BadRequest);
        var (_, challenge) = await PasswordStepAsync(host, username);
        Assert.Equal("Auth:TwoFactorCodeInvalid", await SecondStepErrorAsync(host, new { Token = challenge, RecoveryCode = old[0] }));
    }

    [Fact]
    public async Task Recovery_consumption_and_regeneration_preserve_the_new_code_set()
    {
        var (host, clock) = CreateHost(); using var _ = host;
        var username = await CreateUserAsync(host, "tfa_consume_regen");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        var secret = await BeginSetupAsync(session.Client); var old = await EnableAsync(session.Client, secret, clock); Step(clock);
        var (_, token) = await PasswordStepAsync(host, username);
        using var login = ProjectWebApplicationFactory.CreateProjectClient(host);
        var regenerate = session.Client.PostAsJsonAsync("/api/v1/auth/me/two-factor/recovery-codes", new { Code = Code(secret, clock) });
        var consume = login.PostAsJsonAsync("/api/v1/auth/two-factor", new { Token = token, RecoveryCode = old[0] });
        var results = await Task.WhenAll(regenerate, consume);
        Assert.Equal(HttpStatusCode.OK, results[0].StatusCode);
        Assert.True(results[1].StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest);
        using var codes = JsonDocument.Parse(await results[0].Content.ReadAsStringAsync());
        var newCode = codes.RootElement.GetProperty("recoveryCodes")[0].GetString();
        var (_, fresh) = await PasswordStepAsync(host, username);
        using var completed = await SecondStepAsync(host, fresh, new { Token = fresh, RecoveryCode = newCode });
        var status = await completed.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/two-factor");
        Assert.Equal(RecoveryCodes.Count - 1, status.GetProperty("recoveryCodesLeft").GetInt32());
    }

    [Fact]
    public async Task Failed_recovery_code_audit_rolls_back_the_code_set_and_totp_replay_step()
    {
        var fail = false;
        var (host, clock) = CreateHost(services => {
            var descriptor = services.Last(x => x.ServiceType == typeof(IOperationRecorder)); services.Remove(descriptor);
            services.AddTransient<IOperationRecorder>(sp => new RegenerationAuditFailure((IOperationRecorder)(descriptor.ImplementationFactory?.Invoke(sp) ?? ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!)), () => fail));
        }); using var _ = host;
        var username = await CreateUserAsync(host, "tfa_regen_rollback");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        var secret = await BeginSetupAsync(session.Client); await EnableAsync(session.Client, secret, clock); Step(clock);
        fail = true;
        using var result = await session.Client.PostAsJsonAsync("/api/v1/auth/me/two-factor/recovery-codes", new { Code = Code(secret, clock) });
        Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
        fail = false;
        // 同一个 TOTP 仍可成功重发，证明失败事务没有消费重放步序。
        using var retried = await session.Client.PostAsJsonAsync("/api/v1/auth/me/two-factor/recovery-codes", new { Code = Code(secret, clock) });
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
    }

    private sealed class RegenerationAuditFailure(IOperationRecorder inner, Func<bool> enabled) : IOperationRecorder
    {
        public Task RecordSucceededAsync(string action, OperationTarget target, string authorizationBasis, CancellationToken cancellationToken = default)
            => enabled() && action == OperationRecordActions.AuthTwoFactorRecoveryCodesRegenerated ? throw new InvalidOperationException("Injected recovery audit failure.") : inner.RecordSucceededAsync(action, target, authorizationBasis, cancellationToken);
        public Task RecordFailedAsync(string action, OperationTarget target, string authorizationBasis, OperationFailure failure = default)
            => inner.RecordFailedAsync(action, target, authorizationBasis, failure);
    }

    private (WebApplicationFactory<Program> Host, FakeTimeProvider Clock) CreateHost(Action<IServiceCollection>? configure = null)
    {
        // 从此刻起步：缓存条目按真实时钟回收，假时钟落在过去会让刚写入的挑战被缓存当成已过期。
        // 替换的是 TimeProvider，应用时钟（IClock）与 Cookie、OpenIddict 一并跟随
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            configure?.Invoke(services);
        }));
        return (host, clock);
    }

    private async Task<string> EnableForAsync(WebApplicationFactory<Program> host, string username, FakeTimeProvider clock)
    {
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        var secret = await BeginSetupAsync(session.Client);
        await EnableAsync(session.Client, secret, clock);
        return secret;
    }

    private static async Task<string> BeginSetupAsync(HttpClient client)
    {
        using var response = await client.PostAsync("/api/v1/auth/me/two-factor/setup", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var secret = body.RootElement.GetProperty("secret").GetString()!;
        Assert.StartsWith("otpauth://totp/", body.RootElement.GetProperty("otpAuthUri").GetString());
        return secret;
    }

    private static async Task<List<string>> EnableAsync(HttpClient client, string secret, FakeTimeProvider clock)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/me/two-factor/enable", new { Code = Code(secret, clock) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
    }

    private static async Task<(HttpResponseMessage Response, string Token)> PasswordStepAsync(
        WebApplicationFactory<Program> host, string username)
    {
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/session-login", new { UsernameOrEmail = username, Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("requiresTwoFactor").GetBoolean());
        return (response, body.RootElement.GetProperty("twoFactorToken").GetString()!);
    }

    private static async Task<HttpClient> SecondStepAsync(WebApplicationFactory<Program> host, string token, object input)
    {
        var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/two-factor", input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", CookieOf(response));
        return client;
    }

    private static async Task<string?> SecondStepErrorAsync(WebApplicationFactory<Program> host, object input)
    {
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/two-factor", input);
        Assert.False(response.IsSuccessStatusCode);
        return await ErrorCodeAsync(response);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static string CookieOf(HttpResponseMessage response) =>
        string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';', 2)[0]));

    private static string Code(string secret, FakeTimeProvider clock) =>
        Totp.ComputeCode(Totp.ParseSecret(secret)!, Totp.TimeStepAt(clock.GetUtcNow()));

    private static async Task<string> CreateUserAsync(WebApplicationFactory<Program> host, string prefix)
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
        return username;
    }

    private static async Task WriteSettingAsync(HttpClient client, string name, string? value)
    {
        using var response = await client.PutAsJsonAsync(
            "/api/v1/settings/current-tenant", new { Name = name, Value = value });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>推进一个验证码步长。</summary>
    private static void Step(FakeTimeProvider clock) => clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
}
#endif
