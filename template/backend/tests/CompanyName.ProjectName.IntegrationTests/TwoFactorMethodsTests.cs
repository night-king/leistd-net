#if (Email)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Application.Settings.Provider;
using CompanyName.ProjectName.IntegrationTests.Fixtures;
using Leistd.Email.Abstractions;
using Leistd.Security.OneTimeCodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Time.Testing;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>
/// 登录第二步的多种方式：默认方式按首选与系统顺序给出；邮箱验证码只在用到时才发，只认本次第二步最近发出的码；
/// 组织关闭或邮箱改为未验证时邮箱方式不再可用；邮箱验证码不满足"要求两步验证"。
/// </summary>
/// <remarks>
/// 时钟换成手动推进的：身份验证器同一步的码只认一次，发码有间隔，用例靠推进时钟跨过它们，不靠真实等待。
/// 组织开关在宿主上默认打开，关闭它的用例在结束前恢复。
/// </remarks>
public sealed class TwoFactorMethodsTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Password = "TwoFactorMethods!Passw0rd";

    /// <summary>两种都开、没有首选：默认身份验证器，列表里邮箱在前、恢复码在最后；第一步不发信。</summary>
    [Fact]
    public async Task Without_a_preference_the_authenticator_is_the_default_and_no_email_is_sent()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var user = await CreateUserAsync(host, "tfm_default");
        await EnableAuthenticatorAsync(host, user, clock);
        await EnableEmailAsync(host, user, clock);
        var sentBefore = SentCount(host, user.Email);

        Step(clock);
        var prompt = await PasswordStepAsync(host, user);

        Assert.Equal("authenticator", prompt.DefaultMethod);
        Assert.Equal(["authenticator", "email", "recoveryCode"], prompt.Methods);
        Assert.StartsWith(user.Email[..1], prompt.MaskedEmail);
        Assert.NotEqual(user.Email, prompt.MaskedEmail);
        await SendEmailCodeAsync(host, prompt.Token, user.Email);
        Assert.Equal(sentBefore + 1, SentCount(host, user.Email));
    }

    /// <summary>首选邮箱：默认即邮箱；发码后凭邮件里的码完成登录。</summary>
    [Fact]
    public async Task A_preferred_email_method_is_the_default_and_signs_in_with_the_mailed_code()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var user = await CreateUserAsync(host, "tfm_prefer");
        await EnableAuthenticatorAsync(host, user, clock);
        await EnableEmailAsync(host, user, clock);
        using (var session = await SignInAsync(host, user, clock))
        using (var set = await session.PutAsJsonAsync("/api/v1/auth/me/two-factor/preferred-method", new { Method = "email" }))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            var status = await session.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/two-factor");
            Assert.Equal("email", status.GetProperty("preferredMethod").GetString());
            Assert.Equal("email", status.GetProperty("defaultMethod").GetString());
        }

        Step(clock);
        var prompt = await PasswordStepAsync(host, user);
        Assert.Equal("email", prompt.DefaultMethod);

        var code = await SendEmailCodeAsync(host, prompt.Token, user.Email);
        using var signedIn = await SecondStepAsync(host, new { prompt.Token, EmailCode = code });
        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    /// <summary>
    /// 码只认本次第二步最近发出的那一个：没发过码的挑战交来的码按"已失效"拒绝；发送间隔内重发沿用上一封、不另发信；
    /// 跨过间隔重发之后旧码不认。
    /// </summary>
    [Fact]
    public async Task An_email_code_only_counts_for_the_latest_send_of_its_own_challenge()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var user = await CreateUserAsync(host, "tfm_bound");
        await EnableEmailAsync(host, user, clock);

        var first = await PasswordStepAsync(host, user);
        Assert.Equal("email", first.DefaultMethod);
        Assert.Equal(["email"], first.Methods);
        var firstCode = await SendEmailCodeAsync(host, first.Token, user.Email);

        var second = await PasswordStepAsync(host, user);
        Assert.Equal("Auth:EmailCodeExpired", await SecondStepErrorAsync(host, new { second.Token, EmailCode = firstCode }));

        // 发送间隔之内重发：不另发信，回报剩余间隔
        var sentBefore = SentCount(host, user.Email);
        clock.Advance(TimeSpan.FromSeconds(20));
        using (var tooSoon = await PostEmailCodeAsync(host, first.Token))
        {
            Assert.Equal(HttpStatusCode.OK, tooSoon.StatusCode);
            var body = await tooSoon.Content.ReadFromJsonAsync<JsonElement>();
            Assert.InRange(body.GetProperty("retryAfterSeconds").GetInt32(), 1, 40);
        }

        // 跨过之后重发，前一个码作废；间隔内的那次没有发信
        clock.Advance(TimeSpan.FromMinutes(1));
        var resent = await SendEmailCodeAsync(host, first.Token, user.Email);
        Assert.Equal(sentBefore + 1, SentCount(host, user.Email));
        Assert.Equal("Auth:TwoFactorCodeInvalid", await SecondStepErrorAsync(host, new { first.Token, EmailCode = firstCode }));

        using var signedIn = await SecondStepAsync(host, new { first.Token, EmailCode = resent });
        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    /// <summary>
    /// 邮箱验证码输错与身份验证器同样计数：计入账号的登录失败，换一次第二步也照样累计，到阈值即锁定，
    /// 锁定期内正确的码与发码都被拒；方式此刻不能用的请求不计数。
    /// </summary>
    [Fact]
    public async Task Wrong_email_codes_count_toward_the_account_lockout_across_challenges()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var user = await CreateUserAsync(host, "tfm_lockout");
        await EnableEmailAsync(host, user, clock);
        await ProjectWebApplicationFactory.SetTenantSettingAsync(
            host, SettingConstant.Security.LockoutMaxFailedAttempts, "3");
        try
        {
            // 方式不能用：组织关掉时交来的邮箱码不算猜错，交多少次都不计数
            var unavailable = await PasswordStepAsync(host, user);
            await AllowEmailAsync(host, false);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Assert.Equal("Auth:TwoFactorMethodUnavailable",
                    await SecondStepErrorAsync(host, new { unavailable.Token, EmailCode = "000000" }));
            }

            await AllowEmailAsync(host, true);
            var first = await PasswordStepAsync(host, user);
            await SendEmailCodeAsync(host, first.Token, user.Email);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Assert.Equal("Auth:TwoFactorCodeInvalid",
                    await SecondStepErrorAsync(host, new { first.Token, EmailCode = "000000" }));
            }

            // 换一次第二步，失败次数照样累计：第 3 次即锁定
            clock.Advance(TimeSpan.FromMinutes(1));
            var second = await PasswordStepAsync(host, user);
            var code = await SendEmailCodeAsync(host, second.Token, user.Email);
            Assert.Equal("Auth:UserTemporarilyLockedOut",
                await SecondStepErrorAsync(host, new { second.Token, EmailCode = "000000" }));

            Assert.Equal("Auth:TwoFactorChallengeExpired",
                await SecondStepErrorAsync(host, new { second.Token, EmailCode = code }));
            using var locked = await PostEmailCodeAsync(host, first.Token);
            Assert.Equal("Auth:UserTemporarilyLockedOut", await ErrorCodeAsync(locked));
        }
        finally
        {
            await ProjectWebApplicationFactory.SetTenantSettingAsync(
                host, SettingConstant.Security.LockoutMaxFailedAttempts, null);
        }
    }

    /// <summary>重发不清零这次第二步的尝试次数：用完即作废，只能回到密码那一步。</summary>
    [Fact]
    public async Task Resending_the_email_code_does_not_reset_the_attempts()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var user = await CreateUserAsync(host, "tfm_attempts");
        await EnableEmailAsync(host, user, clock);
        await ProjectWebApplicationFactory.SetTenantSettingAsync(
            host, SettingConstant.Security.LockoutMaxFailedAttempts, "0");
        try
        {
            var prompt = await PasswordStepAsync(host, user);
            await SendEmailCodeAsync(host, prompt.Token, user.Email);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Assert.Equal("Auth:TwoFactorCodeInvalid",
                    await SecondStepErrorAsync(host, new { prompt.Token, EmailCode = "000000" }));
            }

            clock.Advance(TimeSpan.FromMinutes(1));
            var resent = await SendEmailCodeAsync(host, prompt.Token, user.Email);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Assert.Equal("Auth:TwoFactorCodeInvalid",
                    await SecondStepErrorAsync(host, new { prompt.Token, EmailCode = "000000" }));
            }

            // 第 5 次输错用完这次第二步；之后正确的码也不认
            Assert.Equal("Auth:TwoFactorChallengeExpired",
                await SecondStepErrorAsync(host, new { prompt.Token, EmailCode = "000000" }));
            Assert.Equal("Auth:TwoFactorChallengeExpired",
                await SecondStepErrorAsync(host, new { prompt.Token, EmailCode = resent }));
        }
        finally
        {
            await ProjectWebApplicationFactory.SetTenantSettingAsync(
                host, SettingConstant.Security.LockoutMaxFailedAttempts, null);
        }
    }

    /// <summary>默认是身份验证器时也能切到邮箱：此时才发码。</summary>
    [Fact]
    public async Task Switching_to_email_sends_the_code_only_then()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var user = await CreateUserAsync(host, "tfm_switch");
        await EnableAuthenticatorAsync(host, user, clock);
        await EnableEmailAsync(host, user, clock);
        clock.Advance(TimeSpan.FromMinutes(1));
        var sentBefore = SentCount(host, user.Email);

        Step(clock);
        var prompt = await PasswordStepAsync(host, user);

        var code = await SendEmailCodeAsync(host, prompt.Token, user.Email);
        Assert.Equal(sentBefore + 1, SentCount(host, user.Email));
        using var signedIn = await SecondStepAsync(host, new { prompt.Token, EmailCode = code });
        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    /// <summary>组织关闭邮箱方式：只开了它的人回到单步登录；首选邮箱、另有身份验证器的人回落到身份验证器。</summary>
    [Fact]
    public async Task When_the_organization_turns_email_off_the_method_is_no_longer_offered()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var emailOnly = await CreateUserAsync(host, "tfm_off_email");
        await EnableEmailAsync(host, emailOnly, clock);
        var both = await CreateUserAsync(host, "tfm_off_both");
        await EnableAuthenticatorAsync(host, both, clock);
        await EnableEmailAsync(host, both, clock);
        using (var session = await SignInAsync(host, both, clock))
        using (var set = await session.PutAsJsonAsync("/api/v1/auth/me/two-factor/preferred-method", new { Method = "email" }))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        }

        await AllowEmailAsync(host, false);
        try
        {
            using (var single = await SignInAsync(host, emailOnly, clock))
            {
                var status = await single.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/two-factor");
                Assert.True(status.GetProperty("emailEnabled").GetBoolean());
                Assert.Equal("notAllowed", status.GetProperty("emailUnavailableReason").GetString());
                Assert.False(status.TryGetProperty("defaultMethod", out _));
            }

            Step(clock);
            var prompt = await PasswordStepAsync(host, both);
            Assert.Equal("authenticator", prompt.DefaultMethod);
            Assert.DoesNotContain("email", prompt.Methods);
            using var refused = await PostEmailCodeAsync(host, prompt.Token);
            Assert.Equal("Auth:TwoFactorMethodUnavailable", await ErrorCodeAsync(refused));
        }
        finally
        {
            await AllowEmailAsync(host, true);
        }
    }

    /// <summary>邮箱改为未验证：邮箱方式不可用，与组织关闭同样处理。</summary>
    [Fact]
    public async Task An_unverified_email_cannot_be_used_as_a_second_step()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var user = await CreateUserAsync(host, "tfm_unverified");
        await EnableEmailAsync(host, user, clock);

        using (var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword))
        using (var update = await admin.Client.PutAsJsonAsync($"/api/v1/users/{user.Id}", new
        {
            user.Email,
            DisplayName = user.Username,
            IsEmailVerified = false
        }))
        {
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        }

        using var session = await SignInAsync(host, user, clock);
        var status = await session.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/two-factor");
        Assert.Equal("emailNotVerified", status.GetProperty("emailUnavailableReason").GetString());
    }

    /// <summary>组织要求两步验证时，只开邮箱验证码不算满足：会话仍受限。</summary>
    [Fact]
    public async Task Email_codes_do_not_satisfy_a_two_factor_requirement()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var user = await CreateUserAsync(host, "tfm_required");
        await EnableEmailAsync(host, user, clock);
        clock.Advance(TimeSpan.FromMinutes(1));

        await ProjectWebApplicationFactory.SetTenantSettingAsync(host, SettingConstant.Security.RequireTwoFactor, "true");
        try
        {
            var prompt = await PasswordStepAsync(host, user);
            var code = await SendEmailCodeAsync(host, prompt.Token, user.Email);
            using var client = await SecondStepAsync(host, new { prompt.Token, EmailCode = code });
            var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
            Assert.True(me.GetProperty("twoFactorSetupRequired").GetBoolean());
        }
        finally
        {
            await ProjectWebApplicationFactory.SetTenantSettingAsync(host, SettingConstant.Security.RequireTwoFactor, null);
        }
    }

    /// <summary>开启要确认码；关闭后首选回到系统顺序；未设置的方式不能设为首选。</summary>
    [Fact]
    public async Task Turning_email_on_needs_the_mailed_code_and_turning_it_off_clears_the_preference()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var user = await CreateUserAsync(host, "tfm_toggle");
        await EnableAuthenticatorAsync(host, user, clock);
        using var session = await SignInAsync(host, user, clock);

        using (var unset = await session.PutAsJsonAsync("/api/v1/auth/me/two-factor/preferred-method", new { Method = "email" }))
        {
            Assert.Equal("Auth:TwoFactorMethodUnavailable", await ErrorCodeAsync(unset));
        }

        var challengeId = await BeginEmailSetupAsync(session);
        using (var wrong = await session.PostAsJsonAsync("/api/v1/auth/me/two-factor/email/enable", new { challengeId, Code = "000000" }))
        {
            Assert.Equal("Auth:EmailCodeInvalid", await ErrorCodeAsync(wrong));
        }

        var code = await Mailbox(host).NextCodeAsync(user.Email);
        using (var enable = await session.PostAsJsonAsync("/api/v1/auth/me/two-factor/email/enable", new { challengeId, code }))
        {
            Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        }

        using (var prefer = await session.PutAsJsonAsync("/api/v1/auth/me/two-factor/preferred-method", new { Method = "email" }))
        {
            Assert.Equal(HttpStatusCode.OK, prefer.StatusCode);
        }

        using (var disable = await session.PostAsJsonAsync("/api/v1/auth/me/two-factor/email/disable", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        }

        var status = await session.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/two-factor");
        Assert.False(status.GetProperty("emailEnabled").GetBoolean());
        Assert.False(status.TryGetProperty("preferredMethod", out _));
        Assert.Equal("authenticator", status.GetProperty("defaultMethod").GetString());
    }

    /// <summary>管理员重置两步验证时，邮箱验证码与首选一并清掉：只开了邮箱的人也能被重置。</summary>
    [Fact]
    public async Task An_administrator_reset_also_clears_email_codes()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var user = await CreateUserAsync(host, "tfm_reset");
        await EnableEmailAsync(host, user, clock);

        using (var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword))
        {
            var managed = await admin.Client.GetFromJsonAsync<JsonElement>($"/api/v1/users?offset=0&limit=10&keyword={user.Username}");
            Assert.True(managed.GetProperty("items")[0].GetProperty("isTwoFactorEnabled").GetBoolean());
            using var reset = await admin.Client.PostAsJsonAsync($"/api/v1/users/{user.Id}/reset-two-factor", new { });
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        }

        using var session = await SignInAsync(host, user, clock);
        var status = await session.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/two-factor");
        Assert.False(status.GetProperty("emailEnabled").GetBoolean());
    }

    private async Task<(WebApplicationFactory<Program> Host, FakeTimeProvider Clock)> CreateHostAsync()
    {
        // 从此刻起步：缓存条目按这只时钟判断过期，推进它才能跨过发送间隔
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.Configure<MemoryDistributedCacheOptions>(options => options.Clock = new CacheClock(clock));
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<CodeMailbox>();
            services.AddSingleton<IEmailSender>(provider => provider.GetRequiredService<CodeMailbox>());
        }));
        await AllowEmailAsync(host, true);
        return (host, clock);
    }

    private static Task AllowEmailAsync(WebApplicationFactory<Program> host, bool allowed) =>
        ProjectWebApplicationFactory.SetTenantSettingAsync(
            host, SettingConstant.Security.AllowEmailTwoFactor, allowed ? "true" : null);

    private static CodeMailbox Mailbox(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<CodeMailbox>();

    // 信在后台投递：等到一封确定会到的信之后再数，它之前排队的信也都已到达
    private static int SentCount(WebApplicationFactory<Program> host, string email) =>
        Mailbox(host).CodeRecipients.Count(to => string.Equals(to, email, StringComparison.OrdinalIgnoreCase));

    private static async Task<TestUser> CreateUserAsync(WebApplicationFactory<Program> host, string prefix)
    {
        var username = $"{prefix}_{Guid.NewGuid():N}"[..30];
        var email = $"{username}@example.test";
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var create = await admin.Client.PostAsJsonAsync("/api/v1/users", new
        {
            Username = username,
            Email = email,
            Password,
            IsActive = true,
            IsEmailVerified = true
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return new TestUser(id, username, email);
    }

    /// <summary>启用身份验证器，把密钥记在 <paramref name="user"/> 上。</summary>
    private static async Task EnableAuthenticatorAsync(WebApplicationFactory<Program> host, TestUser user, FakeTimeProvider clock)
    {
        using var session = await SignInAsync(host, user, clock);
        using var setup = await session.PostAsJsonAsync("/api/v1/auth/me/two-factor/setup", new { });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var body = await setup.Content.ReadFromJsonAsync<JsonElement>();
        var secret = body.GetProperty("secret").GetString()!;
        using var enable = await session.PostAsJsonAsync("/api/v1/auth/me/two-factor/enable", new
        {
            SetupId = body.GetProperty("setupId").GetGuid(),
            Code = Code(secret, clock)
        });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        user.Secret = secret;
    }

    /// <summary>开启邮箱验证码：会话刚认证，不用另行确认身份。</summary>
    private static async Task EnableEmailAsync(WebApplicationFactory<Program> host, TestUser user, FakeTimeProvider clock)
    {
        using var session = await SignInAsync(host, user, clock);
        var challengeId = await BeginEmailSetupAsync(session);
        var code = await Mailbox(host).NextCodeAsync(user.Email);
        using var enable = await session.PostAsJsonAsync(
            "/api/v1/auth/me/two-factor/email/enable", new { challengeId, code });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        // 下一次发码（登录第二步）不撞上发送间隔
        clock.Advance(TimeSpan.FromMinutes(1));
    }

    /// <summary>登录并返回带会话的客户端：启用了身份验证器的用户再过第二步；会话刚认证，改两步验证设置不用另行确认身份。</summary>
    private static async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> host, TestUser user, FakeTimeProvider clock)
    {
        if (user.Secret is null)
        {
            return (await ProjectWebApplicationFactory.LoginAsync(host, user.Username, Password)).Client;
        }

        Step(clock);
        var prompt = await PasswordStepAsync(host, user);
        return await SecondStepAsync(host, new { prompt.Token, Code = Code(user.Secret, clock) });
    }

    private static async Task<Guid> BeginEmailSetupAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/me/two-factor/email/setup", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challengeId").GetGuid();
    }

    private static async Task<Prompt> PasswordStepAsync(WebApplicationFactory<Program> host, TestUser user)
    {
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/session-login", new { UsernameOrEmail = user.Username, Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("requiresTwoFactor").GetBoolean());
        return new Prompt(
            body.GetProperty("twoFactorToken").GetString()!,
            [.. body.GetProperty("twoFactorMethods").EnumerateArray().Select(method => method.GetString()!)],
            body.GetProperty("defaultTwoFactorMethod").GetString()!,
            body.TryGetProperty("maskedEmail", out var masked) ? masked.GetString() : null);
    }

    private static async Task<HttpResponseMessage> PostEmailCodeAsync(WebApplicationFactory<Program> host, string token)
    {
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        return await client.PostAsJsonAsync("/api/v1/auth/two-factor/email-code", new { token });
    }

    private static async Task<string> SendEmailCodeAsync(WebApplicationFactory<Program> host, string token, string email)
    {
        using var response = await PostEmailCodeAsync(host, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("expiresInSeconds").GetInt32() > 0);
        return await Mailbox(host).NextCodeAsync(email);
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

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        Assert.False(response.IsSuccessStatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static string Code(string secret, FakeTimeProvider clock) =>
        Totp.ComputeCode(Totp.ParseSecret(secret)!, Totp.TimeStepAt(clock.GetUtcNow()));

    /// <summary>推进一个验证码步长。</summary>
    private static void Step(FakeTimeProvider clock) => clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));

    private sealed record TestUser(Guid Id, string Username, string Email)
    {
        /// <summary>身份验证器的密钥；没启用时为 null。</summary>
        public string? Secret { get; set; }
    }

    private sealed record Prompt(string Token, string[] Methods, string DefaultMethod, string? MaskedEmail);

    private sealed class CacheClock(TimeProvider time) : ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }
}
#endif
