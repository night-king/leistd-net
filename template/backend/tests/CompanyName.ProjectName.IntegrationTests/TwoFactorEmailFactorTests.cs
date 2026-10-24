#if (Email)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Application.Auth.Captcha;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Application.Settings.Provider;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Infrastructure.Persistence;
using CompanyName.ProjectName.IntegrationTests.Fixtures;
using Leistd.Email.Abstractions;
using Leistd.Security.OneTimeCodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Time.Testing;
#if (ExternalLogin)
using CompanyName.ProjectName.Domain.Auth.Abstractions;
#endif

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>
/// 邮箱不是独立因子：第一步经邮箱通道得来（邮件找回的口令、按邮箱自动关联的外部登录）或组织要求两步验证时，
/// 第二步只认独立因子；收口之后一种方式都不剩就拒绝登录；换邮箱即作废邮箱因子。
/// </summary>
/// <remarks>时钟手动推进，跨过验证码步长与发送间隔；组织开关在宿主上打开，改动它的用例在结束前恢复。</remarks>
public sealed class TwoFactorEmailFactorTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Password = "EmailFactor!Passw0rd";
    private const string RecoveredPassword = "EmailFactor!Recovered1";

    /// <summary>邮件找回口令后，邮箱码不能完成第二步；用身份验证器完成一次后恢复。</summary>
    [Fact]
    public async Task After_an_email_recovery_only_an_independent_factor_completes_the_second_step()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var user = await CreateUserAsync(host, "ef_recovered");
        await EnableAuthenticatorAsync(host, user, clock);
        await EnableEmailAsync(host, user, clock);

        await RecoverPasswordAsync(host, user, clock);

        Step(clock);
        var prompt = await PasswordStepAsync(host, user);
        Assert.Equal(["authenticator", "recoveryCode"], prompt.Methods);
        Assert.Equal("authenticator", prompt.DefaultMethod);
        using (var send = await PostEmailCodeAsync(host, prompt.Token))
        {
            Assert.Equal("Auth:TwoFactorMethodUnavailable", await ErrorCodeAsync(send));
        }

        Assert.Equal("Auth:TwoFactorMethodUnavailable",
            await SecondStepErrorAsync(host, new { prompt.Token, EmailCode = "000000" }));
        using (await SecondStepAsync(host, new { prompt.Token, Code = Code(user.Secret!, clock) }))
        {
        }

        Step(clock);
        var next = await PasswordStepAsync(host, user);
        Assert.Contains("email", next.Methods);
    }

    /// <summary>找回时还没有第二步的账号：找回后单步登录，再开启邮箱验证码，此后照常用邮箱码登录，不会被挡在门外。</summary>
    [Fact]
    public async Task Turning_email_on_after_a_recovery_without_any_second_step_keeps_sign_in_possible()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var user = await CreateUserAsync(host, "ef_recovered_plain");
        await RecoverPasswordAsync(host, user, clock);

        await EnableEmailAsync(host, user, clock);

        var prompt = await PasswordStepAsync(host, user);
        Assert.Equal(["email"], prompt.Methods);
    }

    /// <summary>只开了邮箱方式的账号不能自助找回：响应与别的邮箱一样，但不发码。</summary>
    [Fact]
    public async Task An_account_whose_only_second_step_is_email_cannot_recover_by_email()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var emailOnly = await CreateUserAsync(host, "ef_email_only");
        await EnableEmailAsync(host, emailOnly, clock);
        var plain = await CreateUserAsync(host, "ef_plain");

        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        using (var refused = await SendResetCodeAsync(client, emailOnly.Email))
        {
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        }

        using (var accepted = await SendResetCodeAsync(client, plain.Email))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        // 后台按顺序投递：后发的这封到了，先发的那封若存在也已经到了
        await Mailbox(host).NextCodeAsync(plain.Email);
        Assert.DoesNotContain(emailOnly.Email, Mailbox(host).CodeRecipients.Skip(1));
    }

    /// <summary>组织要求两步验证时，有身份验证器的人第二步不给邮箱码；只开邮箱的人照旧交码后进入受限会话。</summary>
    [Fact]
    public async Task With_a_requirement_email_codes_are_not_offered_to_holders_of_an_independent_factor()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var both = await CreateUserAsync(host, "ef_required_both");
        await EnableAuthenticatorAsync(host, both, clock);
        await EnableEmailAsync(host, both, clock);
        var emailOnly = await CreateUserAsync(host, "ef_required_email");
        await EnableEmailAsync(host, emailOnly, clock);

        await ProjectWebApplicationFactory.SetTenantSettingAsync(host, SettingConstant.Security.RequireTwoFactor, "true");
        try
        {
            Step(clock);
            var prompt = await PasswordStepAsync(host, both);
            Assert.Equal(["authenticator", "recoveryCode"], prompt.Methods);
            using (var send = await PostEmailCodeAsync(host, prompt.Token))
            {
                Assert.Equal("Auth:TwoFactorMethodUnavailable", await ErrorCodeAsync(send));
            }

            var emailPrompt = await PasswordStepAsync(host, emailOnly);
            Assert.Equal(["email"], emailPrompt.Methods);
        }
        finally
        {
            await ProjectWebApplicationFactory.SetTenantSettingAsync(host, SettingConstant.Security.RequireTwoFactor, null);
        }
    }

    /// <summary>发码能力不可用时，只开了邮箱方式的人不退回单步登录。</summary>
    [Fact]
    public async Task Without_delivery_an_email_only_account_is_refused_rather_than_let_through()
    {
        var (baseHost, clock) = await CreateHostAsync();
        using var ownedBase = baseHost;
        using var host = baseHost.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Leistd:Security:VerificationCodes:Key"] = ""
            })));
        var user = await CreateUserAsync(host, "ef_no_key");
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            var entity = await db.Set<User>().SingleAsync(u => u.Id == user.Id);
            entity.EnableEmailTwoFactor();
            await db.SaveChangesAsync();
        }

        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/session-login", new { UsernameOrEmail = user.Username, Password });
        Assert.Equal("Auth:EmailVerificationUnavailable", await ErrorCodeAsync(login));
        Assert.False(login.Headers.TryGetValues("Set-Cookie", out var cookies) &&
            cookies.Any(value => value.StartsWith(ProjectWebApplicationFactory.SessionCookieName + "=", StringComparison.Ordinal)));
    }

    /// <summary>本人换邮箱：邮箱方式关闭、指向它的首选清除，其他会话退出，当前会话保留。</summary>
    [Fact]
    public async Task Changing_ones_own_email_turns_the_email_method_off_and_signs_out_other_sessions()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var user = await CreateUserAsync(host, "ef_self_change");
        await EnableAuthenticatorAsync(host, user, clock);
        await EnableEmailAsync(host, user, clock);
        using var other = await SignInAsync(host, user, clock);
        using var current = await SignInAsync(host, user, clock);
        using (var prefer = await current.PutAsJsonAsync("/api/v1/auth/me/two-factor/preferred-method", new { Method = "email" }))
        {
            Assert.Equal(HttpStatusCode.OK, prefer.StatusCode);
        }

        using (var change = await current.PutAsJsonAsync("/api/v1/auth/me", new
        {
            user.Username,
            Email = $"moved_{user.Email}",
            DisplayName = user.Username
        }))
        {
            Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        }

        var status = await current.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/two-factor");
        Assert.False(status.GetProperty("emailEnabled").GetBoolean());
        Assert.False(status.TryGetProperty("preferredMethod", out _));
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    /// <summary>管理员改邮箱（哪怕直接标为已验证）同样作废邮箱方式，并让该用户全部会话退出。</summary>
    [Fact]
    public async Task An_administrator_changing_the_email_turns_the_email_method_off()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var user = await CreateUserAsync(host, "ef_admin_change");
        await EnableEmailAsync(host, user, clock);
        clock.Advance(TimeSpan.FromMinutes(1));
        using var session = await SignInWithEmailCodeAsync(host, user);

        using (var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword))
        using (var update = await admin.Client.PutAsJsonAsync($"/api/v1/users/{user.Id}", new
        {
            Email = $"moved_{user.Email}",
            DisplayName = user.Username,
            IsEmailVerified = true
        }))
        {
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/v1/auth/me")).StatusCode);
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/session-login", new { UsernameOrEmail = user.Username, Password });
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("requiresTwoFactor").GetBoolean());
    }

    /// <summary>
    /// 没有有效的邮件挑战（没发过）时交来的码按"已失效"拒绝，不计入失败；发送间隔内重新进入第二步，
    /// 沿用仍有效的上一封码。
    /// </summary>
    [Fact]
    public async Task A_code_without_a_live_email_challenge_is_expired_and_not_counted()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var user = await CreateUserAsync(host, "ef_expired");
        await EnableEmailAsync(host, user, clock);
        await ProjectWebApplicationFactory.SetTenantSettingAsync(
            host, SettingConstant.Security.LockoutMaxFailedAttempts, "2");
        try
        {
            var first = await PasswordStepAsync(host, user);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Assert.Equal("Auth:EmailCodeExpired",
                    await SecondStepErrorAsync(host, new { first.Token, EmailCode = "000000" }));
            }

            using (var send = await PostEmailCodeAsync(host, first.Token))
            {
                Assert.Equal(HttpStatusCode.OK, send.StatusCode);
            }

            var code = await Mailbox(host).NextCodeAsync(user.Email);

            // 上一封还在发送间隔内：重新进入第二步时沿用它，原来那封里的码照样能用
            var again = await PasswordStepAsync(host, user);
            using (var reuse = await PostEmailCodeAsync(host, again.Token))
            {
                Assert.Equal(HttpStatusCode.OK, reuse.StatusCode);
            }

            using var signedIn = await SecondStepAsync(host, new { again.Token, EmailCode = code });
            Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/api/v1/auth/me")).StatusCode);
        }
        finally
        {
            await ProjectWebApplicationFactory.SetTenantSettingAsync(
                host, SettingConstant.Security.LockoutMaxFailedAttempts, null);
        }
    }

    /// <summary>找回口令占用的发送间隔不挡登录第二步的发码。</summary>
    [Fact]
    public async Task A_password_reset_send_does_not_block_the_sign_in_code()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var user = await CreateUserAsync(host, "ef_rate");
        await EnableEmailAsync(host, user, clock);

        var prompt = await PasswordStepAsync(host, user);
        using (var client = ProjectWebApplicationFactory.CreateProjectClient(host))
        using (var reset = await SendResetCodeAsync(client, user.Email))
        {
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        }

        using var send = await PostEmailCodeAsync(host, prompt.Token);
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
    }

    /// <summary>第二步入参恰好一种：多给一种是校验错误，不按优先级挑一个。</summary>
    [Fact]
    public async Task The_second_step_takes_exactly_one_kind_of_code()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var user = await CreateUserAsync(host, "ef_one_kind");
        await EnableAuthenticatorAsync(host, user, clock);

        Step(clock);
        var prompt = await PasswordStepAsync(host, user);
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/two-factor", new
        {
            prompt.Token,
            Code = Code(user.Secret!, clock),
            RecoveryCode = "aaaa-bbbb"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await ErrorCodeAsync(response));
    }

    /// <summary>设置首选方式记操作记录。</summary>
    [Fact]
    public async Task Setting_the_preferred_method_is_recorded()
    {
        var (host, clock) = await CreateHostAsync();
        using var ownedHost = host;
        var user = await CreateUserAsync(host, "ef_prefer_rec");
        await EnableAuthenticatorAsync(host, user, clock);
        await EnableEmailAsync(host, user, clock);
        using (var session = await SignInAsync(host, user, clock))
        using (var prefer = await session.PutAsJsonAsync("/api/v1/auth/me/two-factor/preferred-method", new { Method = "email" }))
        {
            Assert.Equal(HttpStatusCode.OK, prefer.StatusCode);
        }

        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        Assert.Equal(1, await OperationRecordQueries.CountSucceededAsync(
            host, admin.Client, OperationRecordActions.AuthTwoFactorPreferredMethodChanged, user.Id.ToString()));
    }
#if (ExternalLogin)

    /// <summary>
    /// 外部登录按邮箱自动关联进来时，邮箱码不能完成第二步；用独立因子完成一次后恢复。
    /// 只开了邮箱方式的账号经这样的连接登录，一种方式都不剩，拒绝而不是退回单步。
    /// </summary>
    [Fact]
    public async Task An_external_sign_in_linked_by_email_needs_an_independent_factor()
    {
        var both = await NewUserForExternalAsync("ef_ext_both", authenticator: true);
        using (both.Host)
        {
            var prompt = await ExternalStepAsync(both.Host, both.Backchannel);
            Assert.Equal(["authenticator", "recoveryCode"], prompt.GetProperty("twoFactorMethods").EnumerateArray().Select(m => m.GetString()));
            var token = prompt.GetProperty("twoFactorToken").GetString()!;
            Step(both.Clock);
            using (await SecondStepAsync(both.Host, new { token, Code = Code(both.User.Secret!, both.Clock) }))
            {
            }

            Step(both.Clock);
            var next = await ExternalStepAsync(both.Host, both.Backchannel);
            Assert.Contains("email", next.GetProperty("twoFactorMethods").EnumerateArray().Select(m => m.GetString()));
        }

        var emailOnly = await NewUserForExternalAsync("ef_ext_email", authenticator: false);
        using (emailOnly.Host)
        {
            using var client = ProjectWebApplicationFactory.CreateProjectClient(emailOnly.Host);
            var flow = await ExternalOAuthBackchannel.StartAsync(client);
            client.DefaultRequestHeaders.Add("Cookie", flow.Cookie);
            using var complete = await client.PostAsJsonAsync("/api/v1/external-auth/github/complete", new { });
            Assert.Equal("Auth:TwoFactorIndependentFactorRequired", await ErrorCodeAsync(complete));
        }
    }

    private async Task<(WebApplicationFactory<Program> Host, FakeTimeProvider Clock, TestUser User, ExternalOAuthBackchannel Backchannel)>
        NewUserForExternalAsync(string prefix, bool authenticator)
    {
        var backchannel = new ExternalOAuthBackchannel();
        var (host, clock) = await CreateHostAsync(backchannel);
        var user = await CreateUserAsync(host, prefix);
        if (authenticator)
        {
            await EnableAuthenticatorAsync(host, user, clock);
        }

        await EnableEmailAsync(host, user, clock);
        backchannel.User = new ExternalUserInfo
        {
            ProviderId = $"{prefix}-{Guid.NewGuid():N}",
            ProviderAccountLabel = prefix,
            SuggestedUsername = prefix,
            Email = user.Email,
            EmailVerified = true
        };
        return (host, clock, user, backchannel);
    }

    private static async Task<JsonElement> ExternalStepAsync(WebApplicationFactory<Program> host, ExternalOAuthBackchannel backchannel)
    {
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        var flow = await ExternalOAuthBackchannel.StartAsync(client);
        client.DefaultRequestHeaders.Add("Cookie", flow.Cookie);
        using var complete = await client.PostAsJsonAsync("/api/v1/external-auth/github/complete", new { });
        Assert.True(complete.IsSuccessStatusCode, await complete.Content.ReadAsStringAsync());
        var body = await complete.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("requiresTwoFactor").GetBoolean());
        return body;
    }
#endif

    private async Task<(WebApplicationFactory<Program> Host, FakeTimeProvider Clock)> CreateHostAsync(
#if (ExternalLogin)
        ExternalOAuthBackchannel? backchannel = null
#endif
        )
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
#if (ExternalLogin)
        var baseHost = backchannel?.CreateHost(factory) ?? factory;
#else
        WebApplicationFactory<Program> baseHost = factory;
#endif
        var host = baseHost.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.Configure<MemoryDistributedCacheOptions>(options => options.Clock = new CacheClock(clock));
            services.RemoveAll<ICaptchaVerifier>();
            services.AddSingleton<ICaptchaVerifier, AcceptingCaptcha>();
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<CodeMailbox>();
            services.AddSingleton<IEmailSender>(provider => provider.GetRequiredService<CodeMailbox>());
        }));
        await ProjectWebApplicationFactory.SetTenantSettingAsync(
            host, SettingConstant.Security.AllowEmailTwoFactor, "true");
        return (host, clock);
    }

    private static CodeMailbox Mailbox(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<CodeMailbox>();

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

    private static async Task EnableEmailAsync(WebApplicationFactory<Program> host, TestUser user, FakeTimeProvider clock)
    {
        using var session = await SignInAsync(host, user, clock);
        using var begin = await session.PostAsJsonAsync("/api/v1/auth/me/two-factor/email/setup", new { });
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        var challengeId = (await begin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challengeId").GetGuid();
        var code = await Mailbox(host).NextCodeAsync(user.Email);
        using var enable = await session.PostAsJsonAsync(
            "/api/v1/auth/me/two-factor/email/enable", new { challengeId, code });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        user.EmailEnabled = true;
        clock.Advance(TimeSpan.FromMinutes(1));
    }

    /// <summary>经邮件找回把口令换成 <see cref="RecoveredPassword"/>，之后的登录都用它。</summary>
    private static async Task RecoverPasswordAsync(WebApplicationFactory<Program> host, TestUser user, FakeTimeProvider clock)
    {
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var send = await SendResetCodeAsync(client, user.Email);
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        var challengeId = (await send.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challengeId").GetGuid();
        var code = await Mailbox(host).NextCodeAsync(user.Email);
        using var reset = await client.PostAsJsonAsync("/api/v1/auth/password-reset", new
        {
            user.Email,
            ChallengeId = challengeId,
            Code = code,
            NewPassword = RecoveredPassword
        });
        Assert.True(reset.IsSuccessStatusCode, await reset.Content.ReadAsStringAsync());
        user.CurrentPassword = RecoveredPassword;
        clock.Advance(TimeSpan.FromMinutes(1));
    }

    private static Task<HttpResponseMessage> SendResetCodeAsync(HttpClient client, string email) =>
        client.PostAsJsonAsync("/api/v1/auth/password-reset/send-code", new
        {
            Email = email,
            CaptchaToken = "accepted",
            CaptchaCode = "accepted"
        });

    /// <summary>登录并返回带会话的客户端：有身份验证器的用户用它过第二步，只开邮箱的用户用邮箱码。</summary>
    private static async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> host, TestUser user, FakeTimeProvider clock)
    {
        if (user.Secret is not null)
        {
            Step(clock);
            var prompt = await PasswordStepAsync(host, user);
            return await SecondStepAsync(host, new { prompt.Token, Code = Code(user.Secret, clock) });
        }

        if (user.EmailEnabled)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            return await SignInWithEmailCodeAsync(host, user);
        }

        return (await ProjectWebApplicationFactory.LoginAsync(host, user.Username, user.CurrentPassword)).Client;
    }

    private static async Task<HttpClient> SignInWithEmailCodeAsync(WebApplicationFactory<Program> host, TestUser user)
    {
        var prompt = await PasswordStepAsync(host, user);
        using (var send = await PostEmailCodeAsync(host, prompt.Token))
        {
            Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        }

        var code = await Mailbox(host).NextCodeAsync(user.Email);
        return await SecondStepAsync(host, new { prompt.Token, EmailCode = code });
    }

    private static async Task<Prompt> PasswordStepAsync(WebApplicationFactory<Program> host, TestUser user)
    {
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/session-login", new { UsernameOrEmail = user.Username, Password = user.CurrentPassword });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("requiresTwoFactor").GetBoolean());
        return new Prompt(
            body.GetProperty("twoFactorToken").GetString()!,
            [.. body.GetProperty("twoFactorMethods").EnumerateArray().Select(method => method.GetString()!)],
            body.GetProperty("defaultTwoFactorMethod").GetString()!);
    }

    private static async Task<HttpResponseMessage> PostEmailCodeAsync(WebApplicationFactory<Program> host, string token)
    {
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        return await client.PostAsJsonAsync("/api/v1/auth/two-factor/email-code", new { token });
    }

    private static async Task<HttpClient> SecondStepAsync(WebApplicationFactory<Program> host, object input)
    {
        var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/two-factor", input);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ",
            response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';', 2)[0])));
        return client;
    }

    private static async Task<string?> SecondStepErrorAsync(WebApplicationFactory<Program> host, object input)
    {
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/two-factor", input);
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

    private static void Step(FakeTimeProvider clock) => clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));

    private sealed record TestUser(Guid Id, string Username, string Email)
    {
        public string? Secret { get; set; }

        public bool EmailEnabled { get; set; }

        public string CurrentPassword { get; set; } = Password;
    }

    private sealed record Prompt(string Token, string[] Methods, string DefaultMethod);

    private sealed class CacheClock(TimeProvider time) : ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }

    private sealed class AcceptingCaptcha : ICaptchaVerifier
    {
        public Task<bool> VerifyAsync(string token, string code, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
#endif
