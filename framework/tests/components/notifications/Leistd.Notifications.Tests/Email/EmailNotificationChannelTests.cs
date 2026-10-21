using Leistd.BackgroundJobs.Queues;
using Leistd.Email.Abstractions;
using Leistd.Notifications.Channels;
using Leistd.Notifications.Dtos;
using Leistd.Notifications.Email;
using Leistd.Notifications.Email.Recipients;
using Leistd.Notifications.Email.Channels;
using Leistd.Notifications.Email.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Notifications.Tests.Email;

/// <summary>邮件渠道：只发到已验证地址、正文按纯文本发送并附上链接、经后台队列异步发送。</summary>
public sealed class EmailNotificationChannelTests
{
    private static (ServiceProvider Provider, CapturingSender Sender) Build(string? verifiedEmail, string? publicBaseUrl = null)
    {
        var sender = new CapturingSender();
        var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{EmailNotificationOptions.SectionName}:PublicBaseUrl"] = publicBaseUrl
                })
                .Build())
            .AddSingleton<DeferredQueue>()
            .AddSingleton<IBackgroundTaskQueue>(sp => sp.GetRequiredService<DeferredQueue>())
            .AddEmailNotifications()
            .AddSingleton<INotificationRecipientResolver>(new FixedResolver(verifiedEmail))
            .AddSingleton<IEmailSender>(sender)
            .BuildServiceProvider();
        return (provider, sender);
    }

    private static async Task DrainAsync(IServiceProvider provider)
    {
        var queue = provider.GetRequiredService<DeferredQueue>();
        foreach (var item in queue.Items)
        {
            using var scope = provider.CreateScope();
            await item(scope.ServiceProvider, CancellationToken.None);
        }
    }

    /// <summary>通知内容可能夹带用户可控文本：按纯文本发出，不当 HTML 解释。</summary>
    [Fact]
    public async Task A_notification_is_mailed_as_plain_text_through_the_queue()
    {
        var (provider, sender) = Build("ada@example.com");
        using var scope = provider.CreateScope();
        var channel = scope.ServiceProvider.GetServices<INotificationChannel>().Single(c => c.Name == EmailNotificationChannel.ChannelName);

        await channel.DeliverAsync("u1", new NotificationOutputDto
        {
            Id = "n1", Title = "Alert", Content = "<script>x</script>", CreationTime = DateTime.UtcNow
        });

        Assert.Empty(sender.Sent);
        await DrainAsync(provider);

        var message = Assert.Single(sender.Sent);
        Assert.Equal(("ada@example.com", "Alert", "<script>x</script>", false),
            (message.To, message.Subject, message.Body, message.IsBodyHtml));
    }

    /// <summary>通知带跳转链接时，收件人在邮件里也要能打开它。</summary>
    [Fact]
    public async Task The_notification_link_is_appended_to_the_mail_body()
    {
        var (provider, sender) = Build("ada@example.com");
        using var scope = provider.CreateScope();
        var channel = scope.ServiceProvider.GetServices<INotificationChannel>().Single(c => c.Name == EmailNotificationChannel.ChannelName);

        await channel.DeliverAsync("u1", new NotificationOutputDto
        {
            Id = "n1", Title = "New sign-in", Content = "A new device signed in.", Link = "https://app.test/settings/security",
            CreationTime = DateTime.UtcNow
        });
        await DrainAsync(provider);

        var message = Assert.Single(sender.Sent);
        Assert.StartsWith("A new device signed in.", message.Body);
        Assert.EndsWith("https://app.test/settings/security", message.Body);
    }

    /// <summary>没有配置站点地址时，相对链接是站内导航，邮件里打不开，不附。</summary>
    [Fact]
    public async Task A_relative_link_is_not_appended_without_a_public_base_url()
    {
        var (provider, sender) = Build("ada@example.com");
        using var scope = provider.CreateScope();
        var channel = scope.ServiceProvider.GetServices<INotificationChannel>().Single(c => c.Name == EmailNotificationChannel.ChannelName);

        await channel.DeliverAsync("u1", new NotificationOutputDto
        {
            Id = "n1", Title = "New sign-in", Content = "A new device signed in.", Link = "/settings/security",
            CreationTime = DateTime.UtcNow
        });
        await DrainAsync(provider);

        Assert.Equal("A new device signed in.", Assert.Single(sender.Sent).Body);
    }

    /// <summary>配置了站点地址时，站内链接拼成绝对地址附上；带路径前缀与哈希路由的站点照写即可。</summary>
    [Theory]
    [InlineData("https://app.test", "/settings/security", "https://app.test/settings/security")]
    [InlineData("https://app.test/portal/", "/settings/security", "https://app.test/portal/settings/security")]
    [InlineData("https://app.test/#", "/settings/security", "https://app.test/#/settings/security")]
    [InlineData("https://app.test", "https://other.test/x", "https://other.test/x")]
    public async Task A_relative_link_is_resolved_against_the_public_base_url(string baseUrl, string link, string expected)
    {
        Assert.EndsWith(expected, (await MailBodyAsync(baseUrl, link))!);
    }

    /// <summary>站点地址只用来补全以 / 开头的站内链接：协议相对地址与其他写法不附。</summary>
    [Theory]
    [InlineData("//evil.test/phish")]
    [InlineData("javascript:alert(1)")]
    [InlineData("settings/security")]
    public async Task Links_that_are_not_site_paths_are_not_appended(string link)
    {
        Assert.Equal("A new device signed in.", await MailBodyAsync("https://app.test", link));
    }

    [Theory]
    [InlineData("app.test")]
    [InlineData("ftp://app.test")]
    public void A_public_base_url_that_is_not_an_http_url_fails_at_startup(string baseUrl)
    {
        var (provider, _) = Build("ada@example.com", baseUrl);

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<EmailNotificationOptions>>().Value);

        Assert.Contains($"{EmailNotificationOptions.SectionName}:PublicBaseUrl", exception.Message);
    }

    private static async Task<string?> MailBodyAsync(string publicBaseUrl, string link)
    {
        var (provider, sender) = Build("ada@example.com", publicBaseUrl);
        using var scope = provider.CreateScope();
        var channel = scope.ServiceProvider.GetServices<INotificationChannel>().Single(c => c.Name == EmailNotificationChannel.ChannelName);

        await channel.DeliverAsync("u1", new NotificationOutputDto
        {
            Id = "n1", Title = "New sign-in", Content = "A new device signed in.", Link = link,
            CreationTime = DateTime.UtcNow
        });
        await DrainAsync(provider);

        return Assert.Single(sender.Sent).Body;
    }

    [Fact]
    public async Task No_verified_address_means_no_mail()
    {
        var (provider, sender) = Build(verifiedEmail: null);
        using var scope = provider.CreateScope();
        var channel = scope.ServiceProvider.GetServices<INotificationChannel>().Single();

        await channel.DeliverAsync("u1", new NotificationOutputDto { Id = "n1", Title = "Alert", CreationTime = DateTime.UtcNow });
        await DrainAsync(provider);

        Assert.Empty(sender.Sent);
    }

    // 先收下工作项、由用例决定何时执行：断言"入队时尚未发送"需要把两步分开
    private sealed class DeferredQueue : IBackgroundTaskQueue
    {
        public List<Func<IServiceProvider, CancellationToken, ValueTask>> Items { get; } = [];

        public ValueTask QueueAsync(Func<IServiceProvider, CancellationToken, ValueTask> workItem, CancellationToken cancellationToken = default)
        {
            Items.Add(workItem);
            return ValueTask.CompletedTask;
        }

        public bool TryQueue(Func<IServiceProvider, CancellationToken, ValueTask> workItem)
        {
            Items.Add(workItem);
            return true;
        }
    }

    private sealed class FixedResolver(string? email) : INotificationRecipientResolver
    {
        public Task<string?> ResolveEmailAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult(email);
    }

    private sealed class CapturingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
