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

        var setup = await BeginSetupAsync(mine.Client);
        var secret = setup.Secret;
        using (var wrong = await mine.Client.PostAsJsonAsync("/api/v1/auth/me/two-factor/enable", new { setup.SetupId, Code = "000000" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }

        var codes = await EnableAsync(mine.Client, setup, clock);

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
        var setup = await BeginSetupAsync(session.Client);
        var secret = setup.Secret;
        var recovery = (await EnableAsync(session.Client, setup, clock))[0];

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
        var setup = await BeginSetupAsync(session.Client);
        var secret = setup.Secret;
        await EnableAsync(session.Client, setup, clock);
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

        var setup = await BeginSetupAsync(session.Client);
        var secret = setup.Secret;
        await EnableAsync(session.Client, setup, clock);
        Step(clock);

        // 已启用又不出示第二因素：不是换绑，按"已启用"拒绝，不计入再认证失败
        using (var setupAgain = await session.Client.PostAsJsonAsync("/api/v1/auth/me/two-factor/setup", new { }))
        {
            Assert.Equal(HttpStatusCode.Conflict, setupAgain.StatusCode);
            Assert.Equal("Auth:TwoFactorAlreadyEnabled", await ErrorCodeAsync(setupAgain));
        }

        // 用过的那一次设置不能再确认：启用时安全版本已换过
        using (var enableAgain = await session.Client.PostAsJsonAsync(
                   "/api/v1/auth/me/two-factor/enable", new { setup.SetupId, Code = Code(secret, clock) }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, enableAgain.StatusCode);
            Assert.Equal("Auth:TwoFactorSetupExpired", await ErrorCodeAsync(enableAgain));
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

            var setup = await BeginSetupAsync(restricted.Client);
        var secret = setup.Secret;
            using var enable = await restricted.Client.PostAsJsonAsync(
                "/api/v1/auth/me/two-factor/enable", new { setup.SetupId, Code = Code(secret, clock) });
            Assert.Equal(HttpStatusCode.OK, enable.StatusCode);

            // 换发的 Cookie 不再受限，服务端会话还是原来那个；旧的受限 Cookie 随之作废（换发时票据存储移除旧票据）
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

        using var reset = await admin.Client.PostAsJsonAsync($"/api/v1/users/{user.GetProperty("id").GetGuid()}/reset-two-factor", new { });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        using var direct = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        Assert.Equal(HttpStatusCode.OK, (await direct.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    // 会话超过"刚认证过"的窗口：绑定验证器要当场出示口令，错的口令按再认证失败处理
    [Fact]
    public async Task Binding_from_an_older_session_requires_the_current_password()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_stale_bind");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        clock.Advance(StaleSessionAge);

        using (var bare = await session.Client.PostAsJsonAsync(SetupUrl, new { }))
        {
            Assert.Equal("Auth:ReauthenticationRequired", await ErrorCodeAsync(bare));
        }

        using (var wrong = await session.Client.PostAsJsonAsync(SetupUrl, new { Password = Password + "x" }))
        {
            Assert.Equal("Security:CurrentPasswordIncorrect", await ErrorCodeAsync(wrong));
        }

        var setup = await BeginSetupAsync(session.Client, new { Password });
        await EnableAsync(session.Client, setup, clock);
    }

    // 受限会话（组织要求而本人未启用）只是"受限"，不等于"刚认证过"：开久了同样要口令
    [Fact]
    public async Task A_restricted_setup_session_still_needs_a_recent_sign_in()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_stale_required");
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        await WriteSettingAsync(admin.Client, SettingConstant.Security.RequireTwoFactor, "true");
        try
        {
            using var restricted = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
            clock.Advance(StaleSessionAge);

            using (var bare = await restricted.Client.PostAsJsonAsync(SetupUrl, new { }))
            {
                Assert.Equal("Auth:ReauthenticationRequired", await ErrorCodeAsync(bare));
            }

            var setup = await BeginSetupAsync(restricted.Client, new { Password });
            using var enable = await restricted.Client.PostAsJsonAsync(
                "/api/v1/auth/me/two-factor/enable", new { setup.SetupId, Code = Code(setup.Secret, clock) });
            Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        }
        finally
        {
            await WriteSettingAsync(admin.Client, SettingConstant.Security.RequireTwoFactor, null);
        }
    }

    // 两个标签页各开始一次设置：只有最近那一次能确认，先开的那一页按"已失效"重来，而不是报"验证码不对"
    [Fact]
    public async Task Only_the_latest_setup_can_be_confirmed()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_two_tabs");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        var first = await BeginSetupAsync(session.Client);
        var second = await BeginSetupAsync(session.Client);

        using (var stale = await session.Client.PostAsJsonAsync(
                   "/api/v1/auth/me/two-factor/enable", new { first.SetupId, Code = Code(first.Secret, clock) }))
        {
            Assert.Equal("Auth:TwoFactorSetupExpired", await ErrorCodeAsync(stale));
        }

        await EnableAsync(session.Client, second, clock);
    }

    // 开始设置之后改过口令：凭据变了，之前开始的设置作废
    [Fact]
    public async Task A_setup_started_before_a_password_change_cannot_be_confirmed()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_stamp_bound");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        var setup = await BeginSetupAsync(session.Client);

        using (var changed = await session.Client.PostAsJsonAsync("/api/v1/auth/change-password", new
               {
                   CurrentPassword = Password,
                   NewPassword = Password + "2",
                   ConfirmPassword = Password + "2"
               }))
        {
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        }

        using var enable = await session.Client.PostAsJsonAsync(
            "/api/v1/auth/me/two-factor/enable", new { setup.SetupId, Code = Code(setup.Secret, clock) });
        Assert.Equal("Auth:TwoFactorSetupExpired", await ErrorCodeAsync(enable));
    }

    // 换绑：出示现有验证器上的码，确认新验证器后旧验证器与旧恢复码一并作废，其他设备退出
    [Fact]
    public async Task Replacing_the_authenticator_retires_the_old_one_and_its_recovery_codes()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_replace");
        using var other = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        var original = await BeginSetupAsync(session.Client);
        var oldCodes = await EnableAsync(session.Client, original, clock);
        using var second = await SignInWithCodeAsync(host, username, original.Secret, clock);
        Step(clock);

        var replacement = await BeginSetupAsync(session.Client, new { Code = Code(original.Secret, clock) });
        Step(clock);
        var newCodes = await EnableAsync(session.Client, replacement, clock);

        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/v1/auth/me")).StatusCode);
        Step(clock);
        var (_, token) = await PasswordStepAsync(host, username);
        Assert.Equal("Auth:TwoFactorCodeInvalid", await SecondStepErrorAsync(host, new { Token = token, Code = Code(original.Secret, clock) }));
        Assert.Equal("Auth:TwoFactorCodeInvalid", await SecondStepErrorAsync(host, new { Token = token, RecoveryCode = oldCodes[0] }));
        using var signedIn = await SecondStepAsync(host, token, new { Token = token, RecoveryCode = newCodes[0] });
    }

    // 验证器丢了、组织又强制两步验证：用恢复码登录，再用另一个恢复码换绑，全程不经过"未启用"
    [Fact]
    public async Task A_lost_authenticator_is_replaced_with_recovery_codes_even_when_two_factor_is_required()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_lost_required");
        var oldCodes = await EnableWithCodesAsync(host, username, clock);
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        await WriteSettingAsync(admin.Client, SettingConstant.Security.RequireTwoFactor, "true");
        try
        {
            var (_, token) = await PasswordStepAsync(host, username);
            using var recovered = await SecondStepAsync(host, token, new { Token = token, RecoveryCode = oldCodes[0] });

            // 恢复码只换得一次重新绑定，不能换出新的恢复码
            using (var regenerate = await recovered.PostAsJsonAsync(RecoveryCodesUrl, new { Code = oldCodes[1] }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, regenerate.StatusCode);
            }

            var replacement = await BeginSetupAsync(recovered, new { RecoveryCode = oldCodes[1] });
            var newCodes = await EnableAsync(recovered, replacement, clock);

            var status = await recovered.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/two-factor");
            Assert.True(status.GetProperty("enabled").GetBoolean());
            Assert.Equal(RecoveryCodes.Count, status.GetProperty("recoveryCodesLeft").GetInt32());
            var (_, next) = await PasswordStepAsync(host, username);
            Assert.Equal("Auth:TwoFactorCodeInvalid", await SecondStepErrorAsync(host, new { Token = next, RecoveryCode = oldCodes[2] }));
            Step(clock);
            using var signedIn = await SecondStepAsync(host, next, new { Token = next, Code = Code(replacement.Secret, clock) });
            Assert.Equal(RecoveryCodes.Count, newCodes.Count);
        }
        finally
        {
            await WriteSettingAsync(admin.Client, SettingConstant.Security.RequireTwoFactor, null);
        }
    }

    // 换绑出示的恢复码不对：按再认证失败拒绝，不开始设置，也不用掉任何恢复码
    [Fact]
    public async Task Replacing_with_a_wrong_recovery_code_starts_nothing()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_replace_wrong");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        var setup = await BeginSetupAsync(session.Client);
        await EnableAsync(session.Client, setup, clock);

        using var rejected = await session.Client.PostAsJsonAsync(SetupUrl, new { RecoveryCode = "AAAA-BBBB-CCCC-DDDD" });
        Assert.Equal("Auth:TwoFactorCodeInvalid", await ErrorCodeAsync(rejected));
        var status = await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/two-factor");
        Assert.Equal(RecoveryCodes.Count, status.GetProperty("recoveryCodesLeft").GetInt32());
    }

    // 管理员不能经用户管理重置自己的两步验证：那会绕过停用要的口令与验证码，也绕过组织的强制策略
    [Fact]
    public async Task An_administrator_cannot_reset_their_own_two_factor()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var adminId = await UserIdAsync(admin.Client, "admin");

        using var reset = await admin.Client.PostAsJsonAsync($"/api/v1/users/{adminId}/reset-two-factor", new { });

        Assert.Equal("User:SelfTwoFactorResetForbidden", await ErrorCodeAsync(reset));
    }

    // 管理员会话不够新时，重置别人的两步验证要出示自己的口令：被盗的管理员会话不能直接拆掉别人的第二道门
    [Fact]
    public async Task Resetting_someone_else_from_an_older_admin_session_requires_the_admin_password()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_reset_stale");
        await EnableForAsync(host, username, clock);
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var userId = await UserIdAsync(admin.Client, username);
        clock.Advance(StaleSessionAge);

        using (var bare = await admin.Client.PostAsJsonAsync($"/api/v1/users/{userId}/reset-two-factor", new { }))
        {
            Assert.Equal("Auth:ReauthenticationRequired", await ErrorCodeAsync(bare));
        }

        Assert.True(await IsTwoFactorEnabledAsync(admin.Client, username));
        using var reset = await admin.Client.PostAsJsonAsync(
            $"/api/v1/users/{userId}/reset-two-factor", new { Password = ProjectWebApplicationFactory.TestAdminPassword });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.False(await IsTwoFactorEnabledAsync(admin.Client, username));
    }

    // 第一次重发停在提交之前、仍持着锁，第二次用同一步的码只能等它提交后再读：步序已用，拒绝
    [Fact]
    public async Task Concurrent_regeneration_accepts_one_totp_step_and_commits_one_new_code_set()
    {
        var gate = new RecorderGate(OperationRecordActions.AuthTwoFactorRecoveryCodesRegenerated);
        var (host, clock) = CreateHost(services => gate.Install(services));
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_regen_race");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        var setup = await BeginSetupAsync(session.Client);
        var secret = setup.Secret;
        var old = await EnableAsync(session.Client, setup, clock);
        Step(clock);
        var code = Code(secret, clock);

        var first = session.Client.PostAsJsonAsync(RecoveryCodesUrl, new { Code = code });
        await gate.Entered;
        var second = session.Client.PostAsJsonAsync(RecoveryCodesUrl, new { Code = code });
        await AssertStillWaitingAsync(second);
        gate.Release();

        using var firstResponse = await first;
        using var secondResponse = await second;
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal("Auth:TwoFactorCodeInvalid", await ErrorCodeAsync(secondResponse));
        var (_, challenge) = await PasswordStepAsync(host, username);
        Assert.Equal("Auth:TwoFactorCodeInvalid", await SecondStepErrorAsync(host, new { Token = challenge, RecoveryCode = old[0] }));
    }

    // 重发停在提交之前、仍持着锁：登录第二步拿旧恢复码只能等它提交后再读，读到的已是新的一组
    [Fact]
    public async Task Recovery_code_sign_in_waits_for_an_in_flight_regeneration()
    {
        var gate = new RecorderGate(OperationRecordActions.AuthTwoFactorRecoveryCodesRegenerated);
        var (host, clock) = CreateHost(services => gate.Install(services));
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_consume_regen");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        var setup = await BeginSetupAsync(session.Client);
        var secret = setup.Secret;
        var old = await EnableAsync(session.Client, setup, clock);
        Step(clock);
        var (_, token) = await PasswordStepAsync(host, username);
        using var login = ProjectWebApplicationFactory.CreateProjectClient(host);

        var regenerate = session.Client.PostAsJsonAsync(RecoveryCodesUrl, new { Code = Code(secret, clock) });
        await gate.Entered;
        var consume = login.PostAsJsonAsync("/api/v1/auth/two-factor", new { Token = token, RecoveryCode = old[0] });
        await AssertStillWaitingAsync(consume);
        gate.Release();

        using var regenerated = await regenerate;
        using var consumed = await consume;
        Assert.Equal(HttpStatusCode.OK, regenerated.StatusCode);
        Assert.Equal("Auth:TwoFactorCodeInvalid", await ErrorCodeAsync(consumed));
        var newCode = (await RecoveryCodesOfAsync(regenerated))[0];
        var (_, fresh) = await PasswordStepAsync(host, username);
        using var completed = await SecondStepAsync(host, fresh, new { Token = fresh, RecoveryCode = newCode });
        var status = await completed.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/two-factor");
        Assert.Equal(RecoveryCodes.Count - 1, status.GetProperty("recoveryCodesLeft").GetInt32());
    }

    // 受限会话转正是同一个服务端会话换发 Cookie，不登记新会话：这个会话若在此期间被撤销，换发出的 Cookie 一样不能用
    [Fact]
    public async Task Completing_setup_from_a_restricted_session_keeps_the_same_server_session()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_same_sid");
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        await WriteSettingAsync(admin.Client, SettingConstant.Security.RequireTwoFactor, "true");
        try
        {
            var signedInAt = clock.GetUtcNow().UtcDateTime;
            using var restricted = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
            clock.Advance(TimeSpan.FromMinutes(5));

            var setup = await BeginSetupAsync(restricted.Client);
            using var enable = await restricted.Client.PostAsJsonAsync(
                "/api/v1/auth/me/two-factor/enable", new { setup.SetupId, Code = Code(setup.Secret, clock) });
            Assert.Equal(HttpStatusCode.OK, enable.StatusCode);

            using var reissued = ProjectWebApplicationFactory.CreateProjectClient(host);
            reissued.DefaultRequestHeaders.Add("Cookie", CookieOf(enable));
            var sessions = (await reissued.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/sessions")).EnumerateArray().ToList();
            var current = Assert.Single(sessions);
            Assert.True(current.GetProperty("isCurrent").GetBoolean());
            Assert.True(current.GetProperty("creationTime").GetDateTime() <= signedInAt.AddSeconds(1),
                "the reissued cookie must point at the session created at sign-in, not a new one");
        }
        finally
        {
            await WriteSettingAsync(admin.Client, SettingConstant.Security.RequireTwoFactor, null);
        }
    }

    // 受限会话只放行完成设置所需的接口：改资料（含换邮箱）不在其中
    [Fact]
    public async Task A_restricted_session_cannot_change_the_profile()
    {
        var (host, _) = CreateHost();
        using var _host = host;
        var username = await CreateUserAsync(host, "tfa_no_profile");
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        await WriteSettingAsync(admin.Client, SettingConstant.Security.RequireTwoFactor, "true");
        try
        {
            using var restricted = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
            using var change = await restricted.Client.PutAsJsonAsync("/api/v1/auth/me",
                new { Username = username, Email = $"attacker-{username}@example.test", CurrentPassword = Password });
            Assert.Equal(HttpStatusCode.Forbidden, change.StatusCode);
            Assert.Equal("Auth:TwoFactorSetupRequired", await ErrorCodeAsync(change));
        }
        finally
        {
            await WriteSettingAsync(admin.Client, SettingConstant.Security.RequireTwoFactor, null);
        }
    }

    // 第二步停在登记会话之前、持着凭据锁：同时改口令要等它登记完再撤销，第二步得到的会话随改口令失效
    [Fact]
    public async Task A_password_change_waits_for_an_in_flight_second_step_and_revokes_its_session()
    {
        var gate = new RecorderGate(OperationRecordActions.AuthLoginSucceeded) { Armed = false };
        var (host, clock) = CreateHost(services => gate.Install(services));
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_pwd_race");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        var setup = await BeginSetupAsync(session.Client);
        await EnableAsync(session.Client, setup, clock);
        Step(clock);
        var (_, token) = await PasswordStepAsync(host, username);
        var login = ProjectWebApplicationFactory.CreateProjectClient(host);

        gate.Armed = true;
        var secondStep = login.PostAsJsonAsync("/api/v1/auth/two-factor", new { Token = token, Code = Code(setup.Secret, clock) });
        await gate.Entered;
        var change = session.Client.PostAsJsonAsync(
            "/api/v1/auth/change-password", new { CurrentPassword = Password, NewPassword = Password + "1", ConfirmPassword = Password + "1" });
        await AssertStillWaitingAsync(change);
        gate.Release();

        using (var signedIn = await secondStep)
        {
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
            login.DefaultRequestHeaders.Add("Cookie", CookieOf(signedIn));
        }
        using (var changed = await change)
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await login.GetAsync("/api/v1/auth/me")).StatusCode);
        login.Dispose();
    }

    // 管理员重置与本人重发改的是同一份凭据：重置要等重发提交后再读，最终一定是停用状态，
    // 不能被重发晚到的写入改回"已启用"
    [Fact]
    public async Task Admin_reset_waits_for_an_in_flight_regeneration_and_leaves_two_factor_off()
    {
        var gate = new RecorderGate(OperationRecordActions.AuthTwoFactorRecoveryCodesRegenerated);
        var (host, clock) = CreateHost(services => gate.Install(services));
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_reset_race");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        var setup = await BeginSetupAsync(session.Client);
        var secret = setup.Secret;
        await EnableAsync(session.Client, setup, clock);
        Step(clock);
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var userId = await UserIdAsync(admin.Client, username);

        var regenerate = session.Client.PostAsJsonAsync(RecoveryCodesUrl, new { Code = Code(secret, clock) });
        await gate.Entered;
        var reset = admin.Client.PostAsJsonAsync($"/api/v1/users/{userId}/reset-two-factor", new { });
        await AssertStillWaitingAsync(reset);
        gate.Release();

        using var regenerated = await regenerate;
        using var resetResponse = await reset;
        Assert.Equal(HttpStatusCode.OK, regenerated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);
        Assert.False(await IsTwoFactorEnabledAsync(admin.Client, username));
        using var direct = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        Assert.Equal(HttpStatusCode.OK, (await direct.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    // 调用期失败：成功记录在提交之前就抛出，整个工作单元回滚——新恢复码没有落库、旧的一组仍然可用，
    // 验证码的步序也没有记下（同一个码还能再重发一次），也没有留下成功记录。
    // 这与"提交之后才写出"的失败不同：数据库存储的成功记录随业务同一事务提交，日志存储在提交后写出、
    // 写不出只报告不回滚（见框架 operation-records 文档），那一类由组件自己的测试覆盖
    [Fact]
    public async Task Regeneration_rolls_back_codes_and_totp_step_when_the_success_record_fails()
    {
        var gate = new RecorderGate(OperationRecordActions.AuthTwoFactorRecoveryCodesRegenerated) { Fail = true };
        var (host, clock) = CreateHost(services => gate.Install(services));
        using var _ = host;
        var username = await CreateUserAsync(host, "tfa_regen_rollback");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        var setup = await BeginSetupAsync(session.Client);
        var secret = setup.Secret;
        var old = await EnableAsync(session.Client, setup, clock);
        Step(clock);
        gate.Release();

        using (var failed = await session.Client.PostAsJsonAsync(RecoveryCodesUrl, new { Code = Code(secret, clock) }))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        }

        // 旧的一组还在：用其中一个完成登录第二步
        var (_, token) = await PasswordStepAsync(host, username);
        using var signedInWithOldCode = await SecondStepAsync(host, token, new { Token = token, RecoveryCode = old[0] });

        gate.Fail = false;
        using var retried = await session.Client.PostAsJsonAsync(RecoveryCodesUrl, new { Code = Code(secret, clock) });
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var userId = await UserIdAsync(admin.Client, username);
        Assert.Equal(1, await OperationRecordQueries.CountSucceededAsync(
            host, admin.Client, OperationRecordActions.AuthTwoFactorRecoveryCodesRegenerated, userId.ToString()));
    }

    private const string RecoveryCodesUrl = "/api/v1/auth/me/two-factor/recovery-codes";
    private const string SetupUrl = "/api/v1/auth/me/two-factor/setup";

    /// <summary>超出"刚认证过"的窗口。</summary>
    private static readonly TimeSpan StaleSessionAge = TimeSpan.FromMinutes(16);

    private sealed record Setup(Guid SetupId, string Secret);

    /// <summary>
    /// 包住真实记录器：指定动作的成功记录停在调用处等放行（或直接抛出）。成功记录在工作单元提交之前调用，
    /// 停在这里等于让业务持着锁、尚未提交，用来把并发交错固定下来，而不是碰运气。
    /// </summary>
    private sealed class RecorderGate(string action)
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Fail { get; set; }

        /// <summary>为 false 时不拦：准备阶段也会经过同一个动作（登录成功）时先关掉，到要拦的那一步再打开。</summary>
        public bool Armed { get; set; } = true;

        public Task Entered => _entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        public void Release() => _release.TrySetResult();

        public void Install(IServiceCollection services)
        {
            var descriptor = services.Last(x => x.ServiceType == typeof(IOperationRecorder));
            services.Remove(descriptor);
            services.AddTransient<IOperationRecorder>(sp => new Recorder(this, (IOperationRecorder)(
                descriptor.ImplementationFactory?.Invoke(sp)
                ?? ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!))));
        }

        private async Task PassAsync(string recorded)
        {
            if (recorded != action || !Armed)
            {
                return;
            }

            Armed = false;

            _entered.TrySetResult();
            await _release.Task;
            if (Fail)
            {
                throw new InvalidOperationException("Injected success record failure.");
            }
        }

        private sealed class Recorder(RecorderGate gate, IOperationRecorder inner) : IOperationRecorder
        {
            public async Task RecordSucceededAsync(
                string action, OperationTarget target, string authorizationBasis, CancellationToken cancellationToken = default)
            {
                await gate.PassAsync(action);
                await inner.RecordSucceededAsync(action, target, authorizationBasis, cancellationToken);
            }

            public Task RecordFailedAsync(
                string action, OperationTarget target, string authorizationBasis, OperationFailure failure = default)
                => inner.RecordFailedAsync(action, target, authorizationBasis, failure);
        }
    }

    /// <summary>另一个请求被同一把锁挡住：放行之前它不能完成。</summary>
    private static async Task AssertStillWaitingAsync(Task request)
    {
        var finished = await Task.WhenAny(request, Task.Delay(TimeSpan.FromMilliseconds(500)));
        Assert.NotSame(request, finished);
    }

    private static async Task<List<string>> RecoveryCodesOfAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
    }

    private static async Task<JsonElement> ManagedUserAsync(HttpClient admin, string username)
    {
        var users = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/users?offset=0&limit=10&keyword={username}");
        return users.GetProperty("items").EnumerateArray().Single(u => u.GetProperty("username").GetString() == username);
    }

    private static async Task<Guid> UserIdAsync(HttpClient admin, string username) =>
        (await ManagedUserAsync(admin, username)).GetProperty("id").GetGuid();

    private static async Task<bool> IsTwoFactorEnabledAsync(HttpClient admin, string username) =>
        (await ManagedUserAsync(admin, username)).GetProperty("isTwoFactorEnabled").GetBoolean();

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

    private async Task<List<string>> EnableWithCodesAsync(WebApplicationFactory<Program> host, string username, FakeTimeProvider clock)
    {
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        var setup = await BeginSetupAsync(session.Client);
        return await EnableAsync(session.Client, setup, clock);
    }

    private static async Task<HttpClient> SignInWithCodeAsync(
        WebApplicationFactory<Program> host, string username, string secret, FakeTimeProvider clock)
    {
        Step(clock);
        var (_, token) = await PasswordStepAsync(host, username);
        return await SecondStepAsync(host, token, new { Token = token, Code = Code(secret, clock) });
    }

    private async Task<string> EnableForAsync(WebApplicationFactory<Program> host, string username, FakeTimeProvider clock)
    {
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        var setup = await BeginSetupAsync(session.Client);
        var secret = setup.Secret;
        await EnableAsync(session.Client, setup, clock);
        return secret;
    }

    /// <summary>开始一次设置（首次启用，或带上 <paramref name="proof"/> 换绑）。</summary>
    private static async Task<Setup> BeginSetupAsync(HttpClient client, object? proof = null)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/me/two-factor/setup", proof ?? new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.StartsWith("otpauth://totp/", body.RootElement.GetProperty("otpAuthUri").GetString());
        return new Setup(body.RootElement.GetProperty("setupId").GetGuid(), body.RootElement.GetProperty("secret").GetString()!);
    }

    private static async Task<List<string>> EnableAsync(HttpClient client, Setup setup, FakeTimeProvider clock)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/me/two-factor/enable", new { setup.SetupId, Code = Code(setup.Secret, clock) });
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
