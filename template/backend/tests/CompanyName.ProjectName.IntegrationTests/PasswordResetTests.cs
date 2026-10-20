#if (LocalIdentity)
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CompanyName.ProjectName.Application.Auth.Captcha;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.Email.Abstractions;
#if (IncludeMultiTenancy)
using Leistd.MultiTenancy.AspNetCore.Options;
#endif
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Time.Testing;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>
/// 忘记口令：发码不承认邮箱是否对应能找回的账号；码绑定账号与发码时的安全版本，凭据一变（含另一次找回）即作废；
/// 找回成功撤销全部会话、解除临时锁定，但不绕过停用、管理员锁定与两步验证。
/// </summary>
public sealed partial class PasswordResetTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Password = "PasswordReset!Old1";
    private const string NewPassword = "PasswordReset!New1";
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 不存在、邮箱未验证、已停用、管理员锁定：响应与可找回的账号逐字段同形，且都收不到信。
    /// 后台队列按入队顺序发送：最后发给可找回账号的那封到了，前面的都已处理完。
    /// </summary>
    [Fact]
    public async Task Accounts_that_cannot_recover_get_the_same_answer_and_no_email()
    {
        using var host = CreateHost();
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        var unverified = await CreateUserAsync(host, "pr_unverified", emailVerified: false);
        var disabled = await CreateUserAsync(host, "pr_disabled");
        var locked = await CreateUserAsync(host, "pr_locked");
        var recoverable = await CreateUserAsync(host, "pr_ok");
        await ChangeUserAsync(host, disabled.Id, user => user.Disable());
        await ChangeUserAsync(host, locked.Id, user => user.Lock());

        var shapes = new List<string>();
        foreach (var email in new[] { $"nobody-{Guid.NewGuid():N}@example.test", unverified.Email, disabled.Email, locked.Email, recoverable.Email })
        {
            using var response = await SendCodeAsync(client, email);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            shapes.Add(string.Join(',', body.RootElement.EnumerateObject().Select(p => $"{p.Name}:{p.Value.ValueKind}")));
        }

        Assert.Single(shapes.Distinct());
        await Mailbox(host).WaitForCodeAsync(recoverable.Email);
        Assert.Equal([recoverable.Email], Mailbox(host).Recipients);
    }

    [Fact]
    public async Task Reset_signs_out_everywhere_and_only_the_new_password_works()
    {
        using var host = CreateHost();
        using var anonymous = ProjectWebApplicationFactory.CreateProjectClient(host);
        var user = await CreateUserAsync(host, "pr_reset");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, user.Username, Password);

        var challenge = await RequestCodeAsync(host, anonymous, user.Email);
        using (var reset = await ResetAsync(anonymous, user.Email, challenge, NewPassword))
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginResponseAsync(anonymous, user.Username, Password)).StatusCode);
        using (await ProjectWebApplicationFactory.LoginAsync(host, user.Username, NewPassword))
        {
        }

        // 同一个码不能用第二次
        using var replay = await ResetAsync(anonymous, user.Email, challenge, "PasswordReset!Third1");
        AssertInvalidCode(replay);
    }

    /// <summary>两个码都已发出：一个用掉之后另一个随安全版本轮换作废，哪怕设回同一个口令。</summary>
    [Fact]
    public async Task The_other_outstanding_code_is_void_after_one_reset()
    {
        using var host = CreateHost();
        using var anonymous = ProjectWebApplicationFactory.CreateProjectClient(host);
        var user = await CreateUserAsync(host, "pr_twocodes");

        var first = await RequestCodeAsync(host, anonymous, user.Email);
        PassSendInterval(host);
        var second = await RequestCodeAsync(host, anonymous, user.Email);

        using (var reset = await ResetAsync(anonymous, user.Email, first, Password))
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        AssertInvalidCode(await ResetAsync(anonymous, user.Email, second, NewPassword));
        using (await ProjectWebApplicationFactory.LoginAsync(host, user.Username, Password))
        {
        }
    }

    /// <summary>
    /// 两个码几乎同时提交：码都对，但核对与改写在该账号的凭据锁内串行，先提交的那次轮换了安全版本，
    /// 后进锁的一次按锁内重读的版本判定，只能失败。锁外读用户、或不取锁时两次都会成功。
    /// </summary>
    [Fact]
    public async Task Two_codes_racing_for_the_same_account_reset_it_only_once()
    {
        var gate = new InterleavingGate();
        using var host = CreateHost(services =>
            gate.GateLock(services, key => key.Contains(":2fa:lock:", StringComparison.Ordinal)));
        using var first = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var second = ProjectWebApplicationFactory.CreateProjectClient(host);
        var user = await CreateUserAsync(host, "pr_lock");
        var a = await RequestCodeAsync(host, first, user.Email);
        PassSendInterval(host);
        var b = await RequestCodeAsync(host, second, user.Email);

        gate.Arm();
        var held = ResetAsync(first, user.Email, a, "PasswordReset!First1");
        await gate.Entered;
        using (var other = await ResetAsync(second, user.Email, b, "PasswordReset!Second1"))
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        gate.Release();

        AssertInvalidCode(await held);
        using (await ProjectWebApplicationFactory.LoginAsync(host, user.Username, "PasswordReset!Second1"))
        {
        }
    }

    /// <summary>发码之后本人改了口令、或管理员重置了口令：此前发出的码都作废。</summary>
    [Theory]
    [InlineData("self")]
    [InlineData("admin")]
    public async Task A_code_issued_before_a_credential_change_is_void(string changedBy)
    {
        using var host = CreateHost();
        using var anonymous = ProjectWebApplicationFactory.CreateProjectClient(host);
        var user = await CreateUserAsync(host, "pr_changed");
        var challenge = await RequestCodeAsync(host, anonymous, user.Email);

        if (changedBy == "self")
        {
            using var session = await ProjectWebApplicationFactory.LoginAsync(host, user.Username, Password);
            using var changed = await session.Client.PostAsJsonAsync("/api/v1/auth/change-password", new
            {
                CurrentPassword = Password,
                NewPassword = "PasswordReset!Self1",
                ConfirmPassword = "PasswordReset!Self1"
            });
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        }
        else
        {
            using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
            using var changed = await admin.Client.PostAsJsonAsync($"/api/v1/users/{user.Id}/reset-password", new
            {
                Password = "PasswordReset!Admin1"
            });
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        }

        AssertInvalidCode(await ResetAsync(anonymous, user.Email, challenge, NewPassword));
    }

    /// <summary>码发给的是那个账号：邮箱随后换给了另一个账号，码也不能落到新主人身上。</summary>
    [Fact]
    public async Task A_code_does_not_follow_the_address_to_another_account()
    {
        using var host = CreateHost();
        using var anonymous = ProjectWebApplicationFactory.CreateProjectClient(host);
        var original = await CreateUserAsync(host, "pr_orig");
        var challenge = await RequestCodeAsync(host, anonymous, original.Email);

        var successor = await CreateUserAsync(host, "pr_next");
        await ChangeUserAsync(host, original.Id, user => user.UpdateProfile(original.Username, $"moved-{original.Email}", null, null));
        await ChangeUserAsync(host, successor.Id, user =>
        {
            user.UpdateProfile(successor.Username, original.Email, null, null);
            user.ConfirmEmail();
        });

        AssertInvalidCode(await ResetAsync(anonymous, original.Email, challenge, NewPassword));
        using (await ProjectWebApplicationFactory.LoginAsync(host, successor.Username, Password))
        {
        }
        using (await ProjectWebApplicationFactory.LoginAsync(host, original.Username, Password))
        {
        }
    }

    /// <summary>同一个码并发提交两次：码在挑战锁内消费，只有一次成功。</summary>
    [Fact]
    public async Task The_same_code_submitted_twice_concurrently_succeeds_once()
    {
        using var host = CreateHost();
        using var first = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var second = ProjectWebApplicationFactory.CreateProjectClient(host);
        var user = await CreateUserAsync(host, "pr_race");
        var challenge = await RequestCodeAsync(host, first, user.Email);

        var responses = await Task.WhenAll(
            ResetAsync(first, user.Email, challenge, NewPassword),
            ResetAsync(second, user.Email, challenge, "PasswordReset!Other1"));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.BadRequest);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    /// <summary>找回解除登录失败触发的临时锁定；停用与管理员锁定在发码之后才发生时，码也不再能用。</summary>
    [Fact]
    public async Task Reset_releases_a_temporary_lockout_but_not_an_administrative_one()
    {
        using var host = CreateHost();
        using var anonymous = ProjectWebApplicationFactory.CreateProjectClient(host);
        var tempLocked = await CreateUserAsync(host, "pr_temp");
        await ChangeUserAsync(host, tempLocked.Id, user => user.Lock(DateTime.UtcNow.AddMinutes(30)));

        var challenge = await RequestCodeAsync(host, anonymous, tempLocked.Email);
        using (var reset = await ResetAsync(anonymous, tempLocked.Email, challenge, NewPassword))
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        using (await ProjectWebApplicationFactory.LoginAsync(host, tempLocked.Username, NewPassword))
        {
        }

        var adminLocked = await CreateUserAsync(host, "pr_admlock");
        var issued = await RequestCodeAsync(host, anonymous, adminLocked.Email);
        await ChangeUserAsync(host, adminLocked.Id, user => user.Lock());
        AssertInvalidCode(await ResetAsync(anonymous, adminLocked.Email, issued, NewPassword));
    }

    /// <summary>找回只换口令、不签发会话：开着两步验证的账号用新口令登录仍要第二步。</summary>
    [Fact]
    public async Task Two_factor_still_applies_after_a_reset()
    {
        using var host = CreateHost();
        using var anonymous = ProjectWebApplicationFactory.CreateProjectClient(host);
        var user = await CreateUserAsync(host, "pr_2fa");
        await ChangeUserAsync(host, user.Id, entity => entity.EnableTwoFactor("protected-secret", [], 0));

        var challenge = await RequestCodeAsync(host, anonymous, user.Email);
        using (var reset = await ResetAsync(anonymous, user.Email, challenge, NewPassword))
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        using var login = await LoginResponseAsync(anonymous, user.Username, NewPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("requiresTwoFactor").GetBoolean());
        Assert.False(login.Headers.Contains("Set-Cookie"));
    }

    /// <summary>注册与找回各用一种用途：注册时发到这个地址的码，不能拿来找回同一地址上账号的口令。</summary>
    [Fact]
    public async Task A_registration_code_cannot_reset_a_password()
    {
        using var host = CreateHost();
        using var anonymous = ProjectWebApplicationFactory.CreateProjectClient(host);
        var email = $"reg-{Guid.NewGuid():N}@example.test";
        Guid registrationChallenge;
        using (var sent = await anonymous.PostAsJsonAsync("/api/v1/auth/send-email-code", new
               {
                   Email = email,
                   CaptchaToken = "accepted",
                   CaptchaCode = "accepted"
               }))
        {
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
            registrationChallenge = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challengeId").GetGuid();
        }
        var registrationCode = await Mailbox(host).WaitForNextCodeAsync(email);
        var user = await CreateUserAsync(host, "pr_purpose");
        await ChangeUserAsync(host, user.Id, entity =>
        {
            entity.UpdateProfile(user.Username, email, null, null);
            entity.ConfirmEmail();
        });

        AssertInvalidCode(await ResetAsync(anonymous, email, new Challenge(registrationChallenge, registrationCode), NewPassword));
        using (await ProjectWebApplicationFactory.LoginAsync(host, user.Username, Password))
        {
        }
    }

    [Fact]
    public async Task Exhausted_attempts_reject_even_the_correct_code()
    {
        using var host = CreateHost();
        using var anonymous = ProjectWebApplicationFactory.CreateProjectClient(host);
        var user = await CreateUserAsync(host, "pr_attempts");
        var challenge = await RequestCodeAsync(host, anonymous, user.Email);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            AssertInvalidCode(await ResetAsync(anonymous, user.Email, challenge with { Code = challenge.Code == "000000" ? "111111" : "000000" }, NewPassword));
        }

        AssertInvalidCode(await ResetAsync(anonymous, user.Email, challenge, NewPassword));
    }

    /// <summary>投递失败不改变响应：失败与否本身就会泄露邮箱对应着账号。</summary>
    [Fact]
    public async Task A_failed_delivery_does_not_change_the_answer()
    {
        using var host = CreateHost();
        using var anonymous = ProjectWebApplicationFactory.CreateProjectClient(host);
        var user = await CreateUserAsync(host, "pr_smtp");
        Mailbox(host).FailNext();

        using var response = await SendCodeAsync(anonymous, user.Email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
#if (IncludeMultiTenancy)

    /// <summary>码按租户隔离：在别的租户提交，既用不了也不消耗它。</summary>
    [Fact]
    public async Task A_code_cannot_be_used_from_another_tenant()
    {
        using var host = CreateHost();
        var tenant = await CreateTenantAsync(host, $"pr-{Guid.NewGuid():N}"[..20]);
        using var hostClient = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var tenantClient = ProjectWebApplicationFactory.CreateProjectClient(host);
        tenantClient.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, tenant.ToString());
        var user = await CreateUserAsync(host, "pr_scope");
        var challenge = await RequestCodeAsync(host, hostClient, user.Email);

        AssertInvalidCode(await ResetAsync(tenantClient, user.Email, challenge, NewPassword));
        using (var reset = await ResetAsync(hostClient, user.Email, challenge, NewPassword))
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
    }
#endif

    private WebApplicationFactory<Program> CreateHost(Action<IServiceCollection>? configure = null) => factory.WithWebHostBuilder(builder =>
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UserRegistration:EnableEmailVerification"] = "true",
                ["UserRegistration:EmailCodeMaxAttempts"] = "5"
            }));
        builder.ConfigureTestServices(services =>
        {
            // 发送间隔与码的有效期按这只假时钟走，缓存条目也用它判断过期：跨过间隔靠推进时钟，不靠真实等待
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(clock);
            services.Configure<MemoryDistributedCacheOptions>(options => options.Clock = new CacheClock(clock));
            services.RemoveAll<ICaptchaVerifier>();
            services.AddSingleton<ICaptchaVerifier, AcceptingCaptcha>();
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<CapturingMailbox>();
            services.AddSingleton<IEmailSender>(provider => provider.GetRequiredService<CapturingMailbox>());
            configure?.Invoke(services);
        });
    });

    private static CapturingMailbox Mailbox(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<CapturingMailbox>();

    private static void PassSendInterval(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<FakeTimeProvider>().Advance(TimeSpan.FromMinutes(1));

    private static Task<HttpResponseMessage> SendCodeAsync(HttpClient client, string email) =>
        client.PostAsJsonAsync("/api/v1/auth/password-reset/send-code", new
        {
            Email = email,
            CaptchaToken = "accepted",
            CaptchaCode = "accepted"
        });

    private static async Task<Challenge> RequestCodeAsync(WebApplicationFactory<Program> host, HttpClient client, string email)
    {
        using var response = await SendCodeAsync(client, email);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var challengeId = body.RootElement.GetProperty("challengeId").GetGuid();
        var code = await Mailbox(host).WaitForNextCodeAsync(email);
        return new Challenge(challengeId, code);
    }

    private static Task<HttpResponseMessage> ResetAsync(HttpClient client, string email, Challenge challenge, string newPassword) =>
        client.PostAsJsonAsync("/api/v1/auth/password-reset", new
        {
            Email = email,
            challenge.ChallengeId,
            challenge.Code,
            NewPassword = newPassword
        });

    private static void AssertInvalidCode(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("Auth:EmailCodeInvalid", body.RootElement.GetProperty("code").GetString());
        }
    }

    private static Task<HttpResponseMessage> LoginResponseAsync(HttpClient client, string username, string password) =>
        client.PostAsJsonAsync("/api/v1/auth/session-login", new { UsernameOrEmail = username, Password = password });

    private static async Task<TestUser> CreateUserAsync(WebApplicationFactory<Program> host, string prefix, bool emailVerified = true)
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
            IsEmailVerified = emailVerified
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return new TestUser(id, username, email);
    }

    // 没有对应接口的状态（管理员锁定、未经邮件的换邮箱）直接改库，与其他用例同一写法
    private static async Task ChangeUserAsync(WebApplicationFactory<Program> host, Guid userId, Action<User> change)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        change(await db.Users.SingleAsync(user => user.Id == userId));
        await db.SaveChangesAsync();
    }
#if (IncludeMultiTenancy)

    private static async Task<Guid> CreateTenantAsync(WebApplicationFactory<Program> host, string name)
    {
        using var hostAdmin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var response = await hostAdmin.Client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = name,
            DisplayName = name,
            AdminEmail = $"admin@{name}.example.test",
            AdminPassword = "Tenant@123456"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
#endif

    private sealed class CacheClock(TimeProvider time) : ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }

    private sealed record TestUser(Guid Id, string Username, string Email);

    private sealed record Challenge(Guid ChallengeId, string Code);

    private sealed class AcceptingCaptcha : ICaptchaVerifier
    {
        public Task<bool> VerifyAsync(string token, string code, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    /// <summary>
    /// 收下每封信里的验证码。找回码在后台发送，取码要等它到达；每个地址按到达顺序排队，
    /// 同一地址的第二个码不会被第一个顶替。
    /// </summary>
    private sealed partial class CapturingMailbox : IEmailSender
    {
        private readonly ConcurrentDictionary<string, BlockingQueue> _inbox = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> _recipients = new();
        private int _failNext;

        public IReadOnlyList<string> Recipients => [.. _recipients];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _failNext, 0) == 1)
            {
                throw new InvalidOperationException("Simulated email delivery failure.");
            }

            var match = CodeRegex().Match(message.Body);
            Assert.True(match.Success, "The email did not contain a six-digit code.");
            _recipients.Enqueue(message.To);
            Queue(message.To).Add(match.Groups[1].Value);
            return Task.CompletedTask;
        }

        public void FailNext() => Interlocked.Exchange(ref _failNext, 1);

        public Task<string> WaitForNextCodeAsync(string email) => Queue(email).TakeAsync();

        /// <summary>等到这个地址至少收到一封，返回最近的码（不出队）。</summary>
        public async Task<string> WaitForCodeAsync(string email)
        {
            var queue = Queue(email);
            return await queue.PeekLastAsync();
        }

        private BlockingQueue Queue(string email) => _inbox.GetOrAdd(email, _ => new BlockingQueue());

        [GeneratedRegex(@">(\d{6})<", RegexOptions.CultureInvariant)]
        private static partial Regex CodeRegex();

        private sealed class BlockingQueue
        {
            private readonly ConcurrentQueue<string> _items = new();
            private readonly SemaphoreSlim _available = new(0);
            private string? _last;

            public void Add(string code)
            {
                _last = code;
                _items.Enqueue(code);
                _available.Release();
            }

            public async Task<string> TakeAsync()
            {
                Assert.True(await _available.WaitAsync(DeliveryTimeout), "No password reset email arrived in time.");
                Assert.True(_items.TryDequeue(out var code));
                return code!;
            }

            public async Task<string> PeekLastAsync()
            {
                Assert.True(await _available.WaitAsync(DeliveryTimeout), "No email arrived in time.");
                _available.Release();
                return _last!;
            }
        }
    }
}
#endif
