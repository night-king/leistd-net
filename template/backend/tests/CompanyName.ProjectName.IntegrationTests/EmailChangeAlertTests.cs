#if (LocalIdentity)
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Application.Auth.SecurityAlerts;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
#if (Email)
using Leistd.Email.Abstractions;
#endif
using Leistd.OperationRecords.Recording;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
#if (Email)
using Microsoft.Extensions.DependencyInjection.Extensions;
#endif

namespace CompanyName.ProjectName.IntegrationTests;

#if (Email)
/// <summary>
/// 换邮箱的提醒：提交之后向被换掉的已验证地址直接发一封账号安全邮件，另发一条普通安全提醒；
/// 未验证的旧地址不收信，回滚不发，发送失败不影响已提交的修改。
/// </summary>
/// <remarks>
/// 断言落在测试替身 <see cref="IEmailSender"/> 实际收到的收件地址上。旧地址邮件在请求内（提交之后）同步发出，
/// 请求返回即可断言；通知的邮件渠道经后台队列发送，要等。两类邮件按主题区分。
/// </remarks>
#else
/// <summary>换邮箱的提醒：提交之后发一条普通安全提醒，回滚不发。</summary>
#endif
public sealed class EmailChangeAlertTests(EmailChangeAlertTests.Host fixture) : IClassFixture<EmailChangeAlertTests.Host>
{
    private const string Password = "MailTests!Passw0rd";
#if (Email)

    /// <summary>旧地址邮件的主题（请求未指定语言、本人未设语言时为英文）。</summary>
    private const string OldAddressSubject = "This address is no longer your account's email";
#endif

    [Theory]
    [InlineData("self")]
    [InlineData("admin")]
    public async Task Changing_the_email_alerts_the_user_once_after_commit(string by)
    {
        var (username, userId, oldEmail) = await CreateUserAsync("mail_ok", emailVerified: true);
        var newEmail = $"new_{oldEmail}";

        using (var changed = await ChangeEmailAsync(by, username, userId, newEmail))
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        Assert.Equal(newEmail, await ReadEmailAsync(userId));
        Assert.Equal(1, fixture.Alerts.Count(userId, SecurityAlertKind.EmailChanged));
#if (Email)
        var notice = Assert.Single(fixture.Mailbox.Sent, message => message.Subject == OldAddressSubject && IsFor(message, oldEmail, newEmail));
        Assert.Equal(oldEmail, notice.To, ignoreCase: true);
        // 文案必须已渲染：取不到词条时本地化器原样返回键
        Assert.DoesNotContain("SecurityAlert:", notice.Body, StringComparison.Ordinal);
#endif
    }

    /// <summary>只改大小写不算换地址：不提醒。</summary>
    [Fact]
    public async Task Changing_only_the_letter_case_sends_nothing()
    {
        var (username, userId, oldEmail) = await CreateUserAsync("mail_case", emailVerified: true);

        using (var changed = await ChangeEmailAsync("admin", username, userId, oldEmail.ToUpperInvariant()))
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        Assert.Equal(0, fixture.Alerts.Count(userId, SecurityAlertKind.EmailChanged));
#if (Email)
        Assert.DoesNotContain(fixture.Mailbox.Sent, message => IsFor(message, oldEmail));
#endif
    }

    [Theory]
    [InlineData("self")]
    [InlineData("admin")]
    public async Task A_rolled_back_change_sends_nothing(string by)
    {
        var (username, userId, oldEmail) = await CreateUserAsync("mail_rb", emailVerified: true);
        var newEmail = $"new_{oldEmail}";

        fixture.Faults.FailRecordingOf = by == "self" ? OperationRecordActions.AuthEmailChanged : OperationRecordActions.UserUpdated;
        try
        {
            using var failed = await ChangeEmailAsync(by, username, userId, newEmail);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        }
        finally
        {
            fixture.Faults.FailRecordingOf = null;
        }

        Assert.Equal(oldEmail, await ReadEmailAsync(userId));
        Assert.Equal(0, fixture.Alerts.Count(userId, SecurityAlertKind.EmailChanged));
#if (Email)
        Assert.DoesNotContain(fixture.Mailbox.Sent, message => IsFor(message, oldEmail, newEmail));
#endif

        // 同一修改提交后照常提醒：上面的"没有"不是因为这条路径根本不发
        using (var changed = await ChangeEmailAsync(by, username, userId, newEmail))
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        Assert.Equal(1, fixture.Alerts.Count(userId, SecurityAlertKind.EmailChanged));
#if (Email)
        Assert.Equal([oldEmail], OldAddressRecipients(oldEmail, newEmail));
#endif
    }
#if (Email)

    /// <summary>旧地址未验证：没有证明它属于本人，不发账号安全邮件；普通安全提醒照发。</summary>
    [Theory]
    [InlineData("self")]
    [InlineData("admin")]
    public async Task An_unverified_old_address_gets_no_mail(string by)
    {
        var (username, userId, oldEmail) = await CreateUserAsync("mail_unv", emailVerified: false);
        var newEmail = $"new_{oldEmail}";

        using (var changed = await ChangeEmailAsync(by, username, userId, newEmail))
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        Assert.Equal(1, fixture.Alerts.Count(userId, SecurityAlertKind.EmailChanged));
        Assert.Empty(OldAddressRecipients(oldEmail, newEmail));
    }

    /// <summary>每次修改都发给它换掉的那个地址，不发给提交之后账号上的当前地址。</summary>
    [Fact]
    public async Task Each_notice_goes_to_the_address_that_change_replaced()
    {
        var (username, userId, first) = await CreateUserAsync("mail_seq", emailVerified: true);
        var second = $"b_{first}";
        var third = $"c_{first}";

        // 管理员换成第二个地址并标为已验证，随后本人再换成第三个
        using (var byAdmin = await ChangeEmailAsync("admin", username, userId, second, markVerified: true))
            Assert.Equal(HttpStatusCode.OK, byAdmin.StatusCode);
        using (var bySelf = await ChangeEmailAsync("self", username, userId, third))
            Assert.Equal(HttpStatusCode.OK, bySelf.StatusCode);

        Assert.Equal(third, await ReadEmailAsync(userId));
        Assert.Equal([first, second], OldAddressRecipients(first, second, third));
        Assert.Equal(2, fixture.Alerts.Count(userId, SecurityAlertKind.EmailChanged));
    }

    /// <summary>修改已经提交：旧地址邮件发不出去只记日志，请求照常成功，普通安全提醒照发。</summary>
    [Theory]
    [InlineData("self")]
    [InlineData("admin")]
    public async Task A_failed_notice_keeps_the_committed_change(string by)
    {
        var (username, userId, oldEmail) = await CreateUserAsync("mail_fail", emailVerified: true);
        var newEmail = $"new_{oldEmail}";

        fixture.Mailbox.FailTo = oldEmail;
        try
        {
            using var changed = await ChangeEmailAsync(by, username, userId, newEmail);
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        }
        finally
        {
            fixture.Mailbox.FailTo = null;
        }

        Assert.Equal(newEmail, await ReadEmailAsync(userId));
        Assert.Equal(1, fixture.Mailbox.Failures(oldEmail));
        Assert.Empty(OldAddressRecipients(oldEmail, newEmail));
        Assert.Equal(1, fixture.Alerts.Count(userId, SecurityAlertKind.EmailChanged));
    }
#endif
#if (Email && !IncludeNotifications)

    /// <summary>未启用通知时旧地址邮件照发；没有通知的邮件渠道，新地址什么也收不到。</summary>
    [Fact]
    public async Task Without_notifications_only_the_old_address_is_mailed()
    {
        var (username, userId, oldEmail) = await CreateUserAsync("mail_only", emailVerified: true);
        var newEmail = $"new_{oldEmail}";

        using (var changed = await ChangeEmailAsync("admin", username, userId, newEmail, markVerified: true))
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var sent = Assert.Single(fixture.Mailbox.Sent, message => IsFor(message, oldEmail, newEmail));
        Assert.Equal(oldEmail, sent.To, ignoreCase: true);
        Assert.Equal(OldAddressSubject, sent.Subject);
    }
#endif
#if (Email && IncludeNotifications)

    /// <summary>
    /// 两路各自投递：旧的已验证地址收到直发的账号安全邮件；普通安全提醒的邮件渠道按偏好发往账号当前的已验证地址。
    /// </summary>
    [Fact]
    public async Task With_notifications_the_old_and_current_addresses_are_mailed_separately()
    {
        var (username, userId, oldEmail) = await CreateUserAsync("mail_two", emailVerified: true);
        var newEmail = $"new_{oldEmail}";

        using (var changed = await ChangeEmailAsync("admin", username, userId, newEmail, markVerified: true))
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        Assert.Equal([oldEmail], OldAddressRecipients(oldEmail, newEmail));
        // 普通通知的邮件经后台队列发送，要等
        var alert = await fixture.Mailbox.WaitForAsync(
            message => IsFor(message, newEmail) && message.Subject != OldAddressSubject,
            TimeSpan.FromSeconds(5));
        Assert.NotNull(alert);
        Assert.DoesNotContain(fixture.Mailbox.Sent, message => IsFor(message, oldEmail) && message.Subject != OldAddressSubject);
    }
#endif

    private async Task<HttpResponseMessage> ChangeEmailAsync(
        string by,
        string username,
        Guid userId,
        string email,
        bool markVerified = false)
    {
        if (by == "self")
        {
            using var self = await ProjectWebApplicationFactory.LoginAsync(fixture.Server, username, Password);
            return await self.Client.PutAsJsonAsync("/api/v1/auth/me", new { Username = username, Email = email });
        }

        using var admin = await ProjectWebApplicationFactory.LoginAsync(fixture.Server, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        return await admin.Client.PutAsJsonAsync($"/api/v1/users/{userId}", new { Email = email, IsEmailVerified = markVerified });
    }

    private async Task<string> ReadEmailAsync(Guid userId)
    {
        using var admin = await ProjectWebApplicationFactory.LoginAsync(fixture.Server, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var user = await admin.Client.GetFromJsonAsync<JsonElement>($"/api/v1/users/{userId}");
        return user.GetProperty("email").GetString()!;
    }

    private async Task<(string Username, Guid UserId, string Email)> CreateUserAsync(string prefix, bool emailVerified)
    {
        var username = $"{prefix}_{Guid.NewGuid():N}"[..30];
        var email = $"{username}@example.test";
        using var admin = await ProjectWebApplicationFactory.LoginAsync(fixture.Server, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var create = await admin.Client.PostAsJsonAsync("/api/v1/users", new
        {
            Username = username,
            Email = email,
            Password,
            IsActive = true,
            IsEmailVerified = emailVerified
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        return (username, body.GetProperty("id").GetGuid(), email);
    }
#if (Email)

    /// <summary>发给这些地址之一的旧地址邮件，按发送顺序列出收件人。</summary>
    private string[] OldAddressRecipients(params string[] addresses) =>
        fixture.Mailbox.Sent
            .Where(message => message.Subject == OldAddressSubject && IsFor(message, addresses))
            .Select(message => message.To)
            .ToArray();

    private static bool IsFor(EmailMessage message, params string[] addresses) =>
        addresses.Contains(message.To, StringComparer.OrdinalIgnoreCase);
#endif

    /// <summary>本类共用的派生宿主：记下发出的安全提醒与邮件，可在成功审计处注入失败。</summary>
    public sealed class Host : IDisposable
    {
        private readonly ProjectWebApplicationFactory factory = new();

        public Host()
        {
            Server = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
#if (Email)
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(Mailbox);
#endif
                Decorate<ISecurityAlertPublisher>(services, inner => new RecordingSecurityAlertPublisher(inner, Alerts));
                Decorate<IOperationRecorder>(services, inner => CredentialChangeTransactionTests.FaultProxy<IOperationRecorder>.Create(
                    inner,
                    (method, args) => method.Name == nameof(IOperationRecorder.RecordSucceededAsync)
                        && Faults.FailRecordingOf is { } action
                        && Equals(args?[0], action)));
            }));
        }

        public WebApplicationFactory<Program> Server { get; }

        public SecurityAlertLog Alerts { get; } = new();

        public InjectedFaults Faults { get; } = new();
#if (Email)

        public CapturingMailbox Mailbox { get; } = new();
#endif

        public void Dispose()
        {
            Server.Dispose();
            factory.Dispose();
        }

        private static void Decorate<TService>(IServiceCollection services, Func<TService, TService> decorate)
            where TService : class
        {
            var descriptor = services.Last(d => d.ServiceType == typeof(TService));
            services.Remove(descriptor);
            services.Add(ServiceDescriptor.Describe(
                typeof(TService),
                provider => decorate((TService)(descriptor.ImplementationInstance
                    ?? descriptor.ImplementationFactory?.Invoke(provider)
                    ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!))),
                descriptor.Lifetime));
        }
    }

    /// <summary>失败注入开关。同一测试类的用例顺序执行，开关在用例内设置并在 finally 里清除。</summary>
    public sealed class InjectedFaults
    {
        public volatile string? FailRecordingOf;
    }

    /// <summary>按用户记下真正交给发布方的提醒种类。</summary>
    public sealed class SecurityAlertLog
    {
        private readonly ConcurrentDictionary<Guid, ConcurrentQueue<SecurityAlertKind>> alerts = new();

        public void Add(Guid userId, SecurityAlertKind kind) => alerts.GetOrAdd(userId, _ => new()).Enqueue(kind);

        public int Count(Guid userId, SecurityAlertKind kind) =>
            alerts.TryGetValue(userId, out var kinds) ? kinds.Count(k => k == kind) : 0;
    }

    private sealed class RecordingSecurityAlertPublisher(ISecurityAlertPublisher inner, SecurityAlertLog log) : ISecurityAlertPublisher
    {
        public Task PublishAsync(Guid userId, SecurityAlert alert, CancellationToken cancellationToken = default)
        {
            log.Add(userId, alert.Kind);
            return inner.PublishAsync(userId, alert, cancellationToken);
        }
    }
#if (Email)

    /// <summary>记下实际交给发送器的每一封信；可让发往某个地址的信失败。</summary>
    public sealed class CapturingMailbox : IEmailSender
    {
        private readonly ConcurrentQueue<EmailMessage> sent = new();
        private readonly ConcurrentDictionary<string, int> failures = new(StringComparer.OrdinalIgnoreCase);

        public volatile string? FailTo;

        public IReadOnlyCollection<EmailMessage> Sent => sent.ToArray();

        public int Failures(string to) => failures.TryGetValue(to, out var count) ? count : 0;

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            if (string.Equals(message.To, FailTo, StringComparison.OrdinalIgnoreCase))
            {
                failures.AddOrUpdate(message.To, 1, (_, count) => count + 1);
                throw new InvalidOperationException("Simulated email delivery failure.");
            }

            sent.Enqueue(message);
            return Task.CompletedTask;
        }

        public async Task<EmailMessage?> WaitForAsync(Func<EmailMessage, bool> match, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (sent.FirstOrDefault(match) is { } found)
                    return found;
                await Task.Delay(100);
            }

            return null;
        }
    }
#endif
}
#endif
