#if (IncludeNotifications)
#if (Email)
using System.Collections.Concurrent;
#endif
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
#if (Email)
using CompanyName.ProjectName.Application.Notifications.Provider;
#endif
using CompanyName.ProjectName.Application.Notifications.Constants;
using CompanyName.ProjectName.Application.Settings.Provider;
#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Infrastructure.Persistence;
#endif
#if (Email)
using Leistd.Email.Abstractions;
using Leistd.Notifications.Email.Recipients;
#endif
using Leistd.Notifications.Publishing;
using Leistd.Notifications.Dtos;
#if (!LocalIdentity)
using Leistd.MultiTenancy.Context;
#endif
#if (LocalIdentity)
using Microsoft.AspNetCore.Mvc.Testing;
#endif
#if (Email)
using Microsoft.AspNetCore.TestHost;
#endif
#if (LocalIdentity)
using Microsoft.EntityFrameworkCore;
#endif
using Microsoft.Extensions.DependencyInjection;
#if (Email)
using Microsoft.Extensions.DependencyInjection.Extensions;
#endif

namespace CompanyName.ProjectName.IntegrationTests;

#if (LocalIdentity)
/// <summary>通知偏好与安全提醒：本人按"类别 × 渠道"决定收什么；安全提醒的站内通知关不掉；邮件只发已验证的邮箱。</summary>
#else
/// <summary>通知偏好：本人决定是否在站内接收系统通知。资源服务不发邮件，没有邮件渠道与对应偏好。</summary>
#endif
public sealed class NotificationPreferencesTests(ProjectWebApplicationFactory factory) : IClassFixture<ProjectWebApplicationFactory>
{
#if (LocalIdentity)
    private const string Password = "NotifyTests!Passw0rd";

    [Fact]
    public async Task Password_change_sends_an_in_app_security_alert()
    {
        var (host, _) = CreateHost();
        using var _host = host;
        var (username, _) = await CreateUserAsync(host, "notify_pwd");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);

        await ChangePasswordAsync(session.Client);

        var notifications = await ReadNotificationsAsync(session.Client);
        var alert = Assert.Single(notifications, n => n.GetProperty("type").GetString() == "Security");
        // 文案必须已渲染：取不到词条时本地化器原样返回键
        Assert.DoesNotContain("SecurityAlert:", alert.GetProperty("title").GetString());
        Assert.DoesNotContain("SecurityAlert:", alert.GetProperty("content").GetString());
    }

#if (Email)
    [Fact]
    public async Task Security_alert_email_goes_only_to_a_verified_address()
    {
        var (host, mailbox) = CreateHost();
        using var _host = host;
        var (username, email) = await CreateUserAsync(host, "notify_mail");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);

        // 邮箱未验证：不发。渠道在请求内就决定了收件地址（入队前），所以请求返回时判定已经落定，
        // 不必等后台队列——等也只能证明"观察期内没收到"
        await ChangePasswordAsync(session.Client, Password, Password + "1");
        Assert.True(mailbox.Resolved.TryGetValue(await GetUserIdAsync(host, username), out var resolved));
        Assert.Null(resolved);
        Assert.False(mailbox.Received.ContainsKey(email));

        await ConfirmEmailAsync(host, username);
        await ChangePasswordAsync(session.Client, Password + "1", Password + "2");

        Assert.True(await mailbox.WaitForAsync(email, TimeSpan.FromSeconds(5)));
    }
#endif

    [Fact]
    public async Task Disabling_in_app_system_notifications_keeps_security_alerts()
    {
        var (host, _) = CreateHost();
        using var _host = host;
        var (username, _) = await CreateUserAsync(host, "notify_off");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        using (var off = await session.Client.PutAsJsonAsync(
                   "/api/v1/settings/current-user",
                   new { Name = SettingConstant.Notifications.SystemInApp, Value = "false" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        }

        var userId = (await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetString()!;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<INotificationPublisher>().PublishToUserAsync(
                userId,
                new NotificationInputDto { Title = "System notice", Type = AppNotificationTypes.System });
        }

        await ChangePasswordAsync(session.Client);

        var types = (await ReadNotificationsAsync(session.Client)).Select(n => n.GetProperty("type").GetString()).ToList();
        Assert.DoesNotContain(AppNotificationTypes.System, types);
        Assert.Contains("Security", types);
    }

#if (Email)
    private (WebApplicationFactory<Program> Host, CapturingMailbox Mailbox) CreateHost()
    {
        var mailbox = new CapturingMailbox();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(mailbox);
            services.RemoveAll<INotificationRecipientResolver>();
            services.AddTransient<UserEmailRecipientResolver>();
            services.AddTransient<INotificationRecipientResolver>(provider =>
                new RecordingRecipientResolver(provider.GetRequiredService<UserEmailRecipientResolver>(), mailbox));
        }));
        return (host, mailbox);
    }
#else
    private (WebApplicationFactory<Program> Host, object? Mailbox) CreateHost() => (factory.WithWebHostBuilder(_ => { }), null);
#endif

    private static async Task ChangePasswordAsync(HttpClient client, string current = Password, string next = Password + "x")
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/change-password", new
        {
            CurrentPassword = current,
            NewPassword = next,
            ConfirmPassword = next
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<List<JsonElement>> ReadNotificationsAsync(HttpClient client)
    {
        using var body = JsonDocument.Parse(await client.GetStringAsync("/api/v1/notifications"));
        return body.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static async Task<string> GetUserIdAsync(WebApplicationFactory<Program> host, string username)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        return (await db.Set<User>().SingleAsync(u => u.Username == username)).Id.ToString();
    }

#if (Email)
    private static async Task ConfirmEmailAsync(WebApplicationFactory<Program> host, string username)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        var user = await db.Set<User>().SingleAsync(u => u.Username == username);
        user.ConfirmEmail();
        await db.SaveChangesAsync();
    }
#endif

    private static async Task<(string Username, string Email)> CreateUserAsync(WebApplicationFactory<Program> host, string prefix)
    {
        var username = $"{prefix}_{Guid.NewGuid():N}"[..30];
        var email = $"{username}@example.test";
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var create = await admin.Client.PostAsJsonAsync("/api/v1/users", new
        {
            Username = username,
            Email = email,
            Password,
            IsActive = true
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        return (username, email);
    }

#if (Email)
    /// <summary>
    /// 记下收到的信与邮件渠道为每个用户解析出的收件地址。信经后台队列发送，断言要等；
    /// 收件地址在请求内入队前解析，请求返回即可断言。
    /// </summary>
    private sealed class CapturingMailbox : IEmailSender
    {
        public ConcurrentDictionary<string, EmailMessage> Received { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ConcurrentDictionary<string, string?> Resolved { get; } = new();

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Received[message.To] = message;
            return Task.CompletedTask;
        }

        public async Task<bool> WaitForAsync(string to, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (Received.ContainsKey(to))
                    return true;
                await Task.Delay(100);
            }

            return false;
        }
    }

    /// <summary>经真实的收件地址解析，并把结果记进邮箱。</summary>
    private sealed class RecordingRecipientResolver(UserEmailRecipientResolver inner, CapturingMailbox mailbox) : INotificationRecipientResolver
    {
        public async Task<string?> ResolveEmailAsync(string userId, CancellationToken cancellationToken = default)
        {
            var email = await inner.ResolveEmailAsync(userId, cancellationToken);
            mailbox.Resolved[userId] = email;
            return email;
        }
    }
#endif
#else
    [Fact]
    public async Task Disabling_in_app_system_notifications_stops_them_for_that_user_only()
    {
        var tenantId = ProjectWebApplicationFactory.NewTenantId();
        var quiet = Guid.CreateVersion7();
        var listening = Guid.CreateVersion7();
        using var quietSession = factory.CreateResourceSession(quiet, tenantId);
        using var listeningSession = factory.CreateResourceSession(listening, tenantId);

        // 偏好只有站内一项：资源服务没有邮件渠道
        var preferences = (await quietSession.Client.GetFromJsonAsync<JsonElement>("/api/v1/settings"))
            .EnumerateArray()
            .Select(setting => setting.GetProperty("name").GetString())
            .Where(name => name!.StartsWith("Notifications.", StringComparison.Ordinal))
            .ToList();
        Assert.Equal([SettingConstant.Notifications.SystemInApp], preferences);

        using (var off = await quietSession.Client.PutAsJsonAsync(
                   "/api/v1/settings/current-user",
                   new { Name = SettingConstant.Notifications.SystemInApp, Value = "false" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var publisher = scope.ServiceProvider.GetRequiredService<INotificationPublisher>();
            using (scope.ServiceProvider.GetRequiredService<ICurrentTenant>().Change(tenantId))
            {
                foreach (var userId in new[] { quiet, listening })
                {
                    await publisher.PublishToUserAsync(
                        userId.ToString(),
                        new NotificationInputDto { Title = "System notice", Type = AppNotificationTypes.System });
                }
            }
        }

        Assert.Empty(await ReadNotificationsAsync(quietSession.Client));
        Assert.Single(await ReadNotificationsAsync(listeningSession.Client));
    }

    private static async Task<List<JsonElement>> ReadNotificationsAsync(HttpClient client)
    {
        using var body = JsonDocument.Parse(await client.GetStringAsync("/api/v1/notifications"));
        return body.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }
#endif
}
#endif
