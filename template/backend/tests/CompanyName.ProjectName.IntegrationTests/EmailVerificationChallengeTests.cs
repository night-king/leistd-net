#if (LocalIdentity)
using Leistd.MultiTenancy.AspNetCore.Options;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CompanyName.ProjectName.Application.Auth.Captcha;
using Leistd.Email.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CompanyName.ProjectName.IntegrationTests;

public sealed partial class EmailVerificationChallengeTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    [Fact]
    public async Task Challenge_is_bound_to_the_email_and_cannot_be_replayed()
    {
        using var host = CreateEmailVerificationHost();
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        var email = $"challenge-{Guid.NewGuid():N}@example.test";
        var challenge = await SendChallengeAsync(host, client, email);

        var wrongEmail = await RegisterAsync(
            client,
            $"wrong_{Guid.NewGuid():N}"[..32],
            $"other-{email}",
            challenge);
        Assert.Equal(HttpStatusCode.BadRequest, wrongEmail.StatusCode);

        using var legacyContract = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Username = $"legacy_{Guid.NewGuid():N}"[..32],
            Email = email,
            Password = "VerificationTests!Pw",
            EmailVerificationCode = challenge.Code
        });
        Assert.Equal(HttpStatusCode.BadRequest, legacyContract.StatusCode);

        var success = await RegisterAsync(
            client,
            $"valid_{Guid.NewGuid():N}"[..32],
            email,
            challenge);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);

        var replay = await RegisterAsync(
            client,
            $"replay_{Guid.NewGuid():N}"[..32],
            email,
            challenge);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task Exhausted_attempts_reject_even_the_correct_code()
    {
        using var host = CreateEmailVerificationHost();
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        var email = $"attempts-{Guid.NewGuid():N}@example.test";
        var challenge = await SendChallengeAsync(host, client, email);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var rejected = await RegisterAsync(
                client,
                $"bad{attempt}_{Guid.NewGuid():N}"[..32],
                email,
                challenge with { Code = "000000" });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        var exhausted = await RegisterAsync(
            client,
            $"exhausted_{Guid.NewGuid():N}"[..32],
            email,
            challenge);
        Assert.Equal(HttpStatusCode.BadRequest, exhausted.StatusCode);
    }

    [Fact]
    public async Task Failed_send_releases_the_challenge_and_rate_limit_reservation()
    {
        using var host = CreateEmailVerificationHost();
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        var email = $"send-failure-{Guid.NewGuid():N}@example.test";
        host.Services.GetRequiredService<CapturingEmailSender>().FailNext();

        using var failed = await SendChallengeResponseAsync(client, email);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

        _ = await SendChallengeAsync(host, client, email);
    }

#if (IncludeMultiTenancy)
    [Fact]
    public async Task Challenge_is_scoped_to_its_tenant_and_survives_rejection_elsewhere()
    {
        using var host = CreateEmailVerificationHost();
        var tenantA = await CreateTenantAsync(host, $"otp-a-{Guid.NewGuid():N}"[..30]);
        var tenantB = await CreateTenantAsync(host, $"otp-b-{Guid.NewGuid():N}"[..30]);
        using var clientA = CreateTenantClient(host, tenantA);
        using var clientB = CreateTenantClient(host, tenantB);
        var email = $"shared-{Guid.NewGuid():N}@example.test";
        var challengeA = await SendChallengeAsync(host, clientA, email);

        var crossTenant = await RegisterAsync(
            clientB,
            $"tenantb_{Guid.NewGuid():N}"[..32],
            email,
            challengeA);
        Assert.Equal(HttpStatusCode.BadRequest, crossTenant.StatusCode);

        var ownTenant = await RegisterAsync(
            clientA,
            $"tenanta_{Guid.NewGuid():N}"[..32],
            email,
            challengeA);
        Assert.Equal(HttpStatusCode.OK, ownTenant.StatusCode);
    }
#endif


#if (IncludeMultiTenancy)
    [Fact]
    public async Task Send_rate_limit_is_isolated_between_host_and_tenants()
    {
        using var host = CreateEmailVerificationHost();
        var tenantA = await CreateTenantAsync(host, $"rate-a-{Guid.NewGuid():N}"[..30]);
        var tenantB = await CreateTenantAsync(host, $"rate-b-{Guid.NewGuid():N}"[..30]);
        using var hostClient = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var clientA = CreateTenantClient(host, tenantA);
        using var clientB = CreateTenantClient(host, tenantB);
        var email = $"rate-{Guid.NewGuid():N}@example.test";

        Assert.Equal(HttpStatusCode.OK, (await SendChallengeResponseAsync(hostClient, email)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendChallengeResponseAsync(hostClient, email)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendChallengeResponseAsync(clientA, email)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendChallengeResponseAsync(clientB, email)).StatusCode);
    }
#endif


    /// <summary>已登录用户验证自己当前的邮箱：改了邮箱就回到未验证，验证码只对账号上此刻的邮箱有效。</summary>
    [Fact]
    public async Task Signed_in_user_verifies_email_and_must_reverify_after_changing_it()
    {
        using var host = CreateEmailVerificationHost();
        var username = $"verify_{Guid.NewGuid():N}"[..30];
        const string password = "VerificationTests!Pw1";
        using (var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword))
        {
            using var create = await admin.Client.PostAsJsonAsync("/api/v1/users", new
            {
                Username = username,
                Email = $"{username}@example.test",
                Password = password,
                IsActive = true,
                IsEmailVerified = true
            });
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        }

        using var user = await ProjectWebApplicationFactory.LoginAsync(host, username, password);
        Assert.True(await ReadEmailVerifiedAsync(user.Client));

        // 已验证时不再发码
        using var alreadyVerified = await user.Client.PostAsync("/api/v1/auth/me/email-verification", null);
        Assert.Equal(HttpStatusCode.Conflict, alreadyVerified.StatusCode);
        Assert.Equal("Auth:EmailAlreadyVerified",
            (await alreadyVerified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var newEmail = $"new-{username}@example.test";
        using var change = await user.Client.PutAsJsonAsync("/api/v1/auth/me", new { Username = username, Email = newEmail });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        Assert.False(await ReadEmailVerifiedAsync(user.Client));

        using var send = await user.Client.PostAsync("/api/v1/auth/me/email-verification", null);
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        using var challenge = JsonDocument.Parse(await send.Content.ReadAsStringAsync());
        var challengeId = challenge.RootElement.GetProperty("challengeId").GetGuid();
        var code = host.Services.GetRequiredService<CapturingEmailSender>().GetCode(newEmail);

        var wrongCode = code == "000000" ? "111111" : "000000";
        using var wrong = await user.Client.PostAsJsonAsync("/api/v1/auth/me/email-verification/confirm", new { ChallengeId = challengeId, Code = wrongCode });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.False(await ReadEmailVerifiedAsync(user.Client));

        using var confirm = await user.Client.PostAsJsonAsync("/api/v1/auth/me/email-verification/confirm", new { ChallengeId = challengeId, Code = code });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.True(await ReadEmailVerifiedAsync(user.Client));
    }

    /// <summary>
    /// 确认停在核对验证码处、持着本人的凭据锁：同时改邮箱要等它提交后再改，最终是新地址、未验证——
    /// 旧地址的验证码不能顺带把新地址标成已验证。
    /// </summary>
    [Fact]
    public async Task Confirming_the_old_address_while_the_email_changes_never_verifies_the_new_one()
    {
        var gate = new InterleavingGate();
        using var host = CreateEmailVerificationHost(services =>
            gate.GateLock(services, key => key.Contains(":lock:challenge:", StringComparison.Ordinal)));
        var username = $"race_{Guid.NewGuid():N}"[..30];
        const string password = "VerificationTests!Pw1";
        var oldEmail = $"{username}@example.test";
        using (var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword))
        {
            using var create = await admin.Client.PostAsJsonAsync("/api/v1/users", new
            {
                Username = username,
                Email = oldEmail,
                Password = password,
                IsActive = true
            });
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        }

        using var user = await ProjectWebApplicationFactory.LoginAsync(host, username, password);
        using var send = await user.Client.PostAsync("/api/v1/auth/me/email-verification", null);
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        using var challenge = JsonDocument.Parse(await send.Content.ReadAsStringAsync());
        var challengeId = challenge.RootElement.GetProperty("challengeId").GetGuid();
        var code = host.Services.GetRequiredService<CapturingEmailSender>().GetCode(oldEmail);

        gate.Arm();
        var confirm = user.Client.PostAsJsonAsync("/api/v1/auth/me/email-verification/confirm", new { ChallengeId = challengeId, Code = code });
        await gate.Entered;
        var newEmail = $"new-{oldEmail}";
        var change = user.Client.PutAsJsonAsync("/api/v1/auth/me", new { Username = username, Email = newEmail });
        var finished = await Task.WhenAny(change, Task.Delay(TimeSpan.FromMilliseconds(500)));
        Assert.NotSame(change, finished);
        gate.Release();

        using (var confirmed = await confirm)
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        using (var changed = await change)
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var me = JsonDocument.Parse(await user.Client.GetStringAsync("/api/v1/auth/me"));
        Assert.Equal(newEmail, me.RootElement.GetProperty("email").GetString());
        Assert.False(me.RootElement.GetProperty("isEmailVerified").GetBoolean());
    }

    /// <summary>
    /// 注册时当场验证过这个地址，账号的邮箱就是已验证：同邮箱的外部登录、通知收件人与登录后的提示都按已验证处理。
    /// 关闭邮箱验证时注册只过图形验证码，地址没被验证过，仍是未验证。
    /// </summary>
    [Fact]
    public async Task Registration_confirms_the_email_only_when_its_code_was_verified()
    {
        const string password = "VerificationTests!Pw";
        using var host = CreateEmailVerificationHost();
        using var anonymous = ProjectWebApplicationFactory.CreateProjectClient(host);
        var email = $"confirmed-{Guid.NewGuid():N}@example.test";
        var username = $"confirmed_{Guid.NewGuid():N}"[..32];
        using (var registered = await RegisterAsync(anonymous, username, email, await SendChallengeAsync(host, anonymous, email)))
        {
            Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
            using var body = JsonDocument.Parse(await registered.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("isEmailVerified").GetBoolean());
        }

        using (var session = await ProjectWebApplicationFactory.LoginAsync(host, username, password))
            Assert.True(await ReadEmailVerifiedAsync(session.Client));

        using var withoutVerification = host.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["UserRegistration:EnableEmailVerification"] = "false" })));
        using var captchaClient = ProjectWebApplicationFactory.CreateProjectClient(withoutVerification);
        var plainUsername = $"plain_{Guid.NewGuid():N}"[..32];
        using (var registered = await captchaClient.PostAsJsonAsync("/api/v1/auth/register", new
               {
                   Username = plainUsername,
                   Email = $"plain-{Guid.NewGuid():N}@example.test",
                   Password = password,
                   CaptchaToken = "accepted",
                   CaptchaCode = "accepted"
               }))
        {
            Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
            using var body = JsonDocument.Parse(await registered.Content.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("isEmailVerified").GetBoolean());
        }
    }

    /// <summary>注册时发出的验证码不能拿来验证已有账号的邮箱：挑战绑定用途。</summary>
    [Fact]
    public async Task Registration_code_cannot_verify_an_existing_account_email()
    {
        using var host = CreateEmailVerificationHost();
        using var anonymous = ProjectWebApplicationFactory.CreateProjectClient(host);
        var registrationEmail = $"reg-{Guid.NewGuid():N}@example.test";
        var registration = await SendChallengeAsync(host, anonymous, registrationEmail);

        // 把一个已有账号的邮箱改成注册挑战对应的那个地址，再拿注册验证码去确认
        var username = $"purpose_{Guid.NewGuid():N}"[..30];
        const string password = "VerificationTests!Pw1";
        using (var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword))
        {
            using var create = await admin.Client.PostAsJsonAsync("/api/v1/users", new
            {
                Username = username,
                Email = registrationEmail,
                Password = password,
                IsActive = true
            });
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        }

        using var user = await ProjectWebApplicationFactory.LoginAsync(host, username, password);
        using var confirm = await user.Client.PostAsJsonAsync("/api/v1/auth/me/email-verification/confirm", new
        {
            registration.ChallengeId,
            registration.Code
        });

        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.False(await ReadEmailVerifiedAsync(user.Client));
    }

    /// <summary>
    /// 注册发码前的占用判定与建号同一口径：被删用户仍占着地址，只差大小写的是另一个地址。
    /// 先放行再在建号时撞上，用户就白收了一封验证码。
    /// </summary>
    [Fact]
    public async Task Registration_code_is_refused_for_an_email_held_by_a_deleted_user()
    {
        using var host = CreateEmailVerificationHost();
        using var anonymous = ProjectWebApplicationFactory.CreateProjectClient(host);
        var username = $"held_{Guid.NewGuid():N}"[..24];
        var email = $"{username}@example.test";
        using (var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword))
        {
            using var create = await admin.Client.PostAsJsonAsync("/api/v1/users", new
            {
                Username = username,
                Email = email,
                Password = "VerificationTests!Pw1",
                IsActive = true
            });
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
            var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            using var delete = await admin.Client.DeleteAsync($"/api/v1/users/{id}");
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        }

        using (var refused = await SendChallengeResponseAsync(anonymous, email))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Equal("User:EmailTaken", body.RootElement.GetProperty("code").GetString());
        }

        // 被拒时没有发出验证码，也没有占用发送间隔
        Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<CapturingEmailSender>().GetCode(email));

        var variant = email.ToUpperInvariant();
        await SendChallengeAsync(host, anonymous, variant);
    }

    private static async Task<bool> ReadEmailVerifiedAsync(HttpClient client)
    {
        using var me = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        return me.RootElement.GetProperty("isEmailVerified").GetBoolean();
    }

    private WebApplicationFactory<Program> CreateEmailVerificationHost(Action<IServiceCollection>? configure = null)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["UserRegistration:EnableEmailVerification"] = "true",
                    ["UserRegistration:EmailCodeMaxAttempts"] = "5"
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICaptchaVerifier>();
                services.AddSingleton<ICaptchaVerifier, AcceptingCaptcha>();
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<CapturingEmailSender>();
                services.AddSingleton<IEmailSender>(provider =>
                    provider.GetRequiredService<CapturingEmailSender>());
                configure?.Invoke(services);
            });
        });
    }

    private static async Task<ChallengeCredentials> SendChallengeAsync(
        WebApplicationFactory<Program> host,
        HttpClient client,
        string email)
    {
        using var response = await SendChallengeResponseAsync(client, email);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var challengeId = body.RootElement.GetProperty("challengeId").GetGuid();
        Assert.True(body.RootElement.GetProperty("expiresInSeconds").GetInt32() > 0);
        Assert.True(body.RootElement.GetProperty("retryAfterSeconds").GetInt32() > 0);
        var code = host.Services.GetRequiredService<CapturingEmailSender>().GetCode(email);
        return new ChallengeCredentials(challengeId, code);
    }

    private static Task<HttpResponseMessage> SendChallengeResponseAsync(HttpClient client, string email)
        => client.PostAsJsonAsync("/api/v1/auth/send-email-code", new
        {
            Email = email,
            CaptchaToken = "accepted",
            CaptchaCode = "accepted"
        });

    private static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client,
        string username,
        string email,
        ChallengeCredentials challenge)
        => client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Username = username,
            Email = email,
            Password = "VerificationTests!Pw",
            EmailVerification = new
            {
                challenge.ChallengeId,
                challenge.Code
            }
        });

    private static HttpClient CreateTenantClient(WebApplicationFactory<Program> host, Guid tenantId)
    {
        var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        client.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, tenantId.ToString());
        return client;
    }

    private static async Task<Guid> CreateTenantAsync(WebApplicationFactory<Program> host, string name)
    {
        using var hostAdmin = await ProjectWebApplicationFactory.LoginAsync(
            host,
            "admin",
            ProjectWebApplicationFactory.TestAdminPassword);
        using var response = await hostAdmin.Client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = name,
            DisplayName = name,
            AdminEmail = $"admin@{name}.example.test",
            AdminPassword = "Tenant@123456"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private sealed record ChallengeCredentials(Guid ChallengeId, string Code);

    private sealed class AcceptingCaptcha : ICaptchaVerifier
    {
        public Task<bool> VerifyAsync(string token, string code, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private sealed partial class CapturingEmailSender : IEmailSender
    {
        private readonly ConcurrentDictionary<string, string> _codes = new(StringComparer.OrdinalIgnoreCase);
        private int _failNext;

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _failNext, 0) == 1)
            {
                throw new InvalidOperationException("Simulated email delivery failure.");
            }

            var match = VerificationCodeRegex().Match(message.Body);
            Assert.True(match.Success, "The verification email did not contain a six-digit code.");
            _codes[message.To] = match.Groups[1].Value;
            return Task.CompletedTask;
        }

        public string GetCode(string email)
            => _codes.TryGetValue(email, out var code)
                ? code
                : throw new InvalidOperationException($"No verification email was sent to '{email}'.");

        public void FailNext() => Interlocked.Exchange(ref _failNext, 1);

        [GeneratedRegex(@">(\d{6})<", RegexOptions.CultureInvariant)]
        private static partial Regex VerificationCodeRegex();
    }
}
#endif
