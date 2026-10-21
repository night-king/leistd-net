using Leistd.BackgroundJobs.Queues;
using Leistd.Email.Abstractions;
using Leistd.Notifications.Channels;
using Leistd.Notifications.Dtos;
using Leistd.Notifications.Email.Recipients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Leistd.Notifications.Email.Options;

namespace Leistd.Notifications.Email.Channels;

/// <summary>通知的邮件渠道：收件人有已验证地址时，经后台队列异步发送。</summary>
/// <remarks>
/// <para>正文按纯文本发送（内容，链接另起一段附上），避免用户可控文本注入 HTML。
/// 需要定制邮件版式时实现自己的 <see cref="INotificationChannel"/>。</para>
/// <para>链接：绝对 http(s) 地址原样附上；以 <c>/</c> 开头的站内链接在配置了 <see cref="EmailNotificationOptions.PublicBaseUrl"/> 时转成绝对地址附上，未配置时不附。</para>
/// <para>队列满时丢弃本封并记警告，不阻塞发布方。</para>
/// </remarks>
/// <param name="recipients">收件人地址解析。</param>
/// <param name="queue">后台任务队列。</param>
/// <param name="options">渠道选项。</param>
/// <param name="logger">日志。</param>
public sealed class EmailNotificationChannel(
    INotificationRecipientResolver recipients,
    IBackgroundTaskQueue queue,
    IOptions<EmailNotificationOptions> options,
    ILogger<EmailNotificationChannel> logger) : INotificationChannel
{
    /// <summary>渠道名，也是通知偏好设置名里的渠道段。</summary>
    public const string ChannelName = "Email";

    /// <inheritdoc />
    public string Name => ChannelName;

    /// <inheritdoc />
    public async Task DeliverAsync(string userId, NotificationOutputDto notification, CancellationToken ct = default)
    {
        if (await recipients.ResolveEmailAsync(userId, ct) is not { Length: > 0 } email)
        {
            return;
        }

        var body = notification.Content ?? notification.Title;
        // 站内相对链接放进邮件打不开：配置了站点地址才转成绝对地址，否则不附
        if (EmailNotificationLinks.Resolve(notification.Link, options.Value.PublicBaseUrl) is { } link)
        {
            body = $"{body}{Environment.NewLine}{Environment.NewLine}{link}";
        }

        var message = new EmailMessage
        {
            To = email,
            Subject = notification.Title,
            Body = body,
            IsBodyHtml = false
        };

        if (!queue.TryQueue((services, token) => new ValueTask(services.GetRequiredService<IEmailSender>().SendAsync(message, token))))
        {
            logger.LogWarning("The background queue is full; the notification email to user {UserId} was dropped.", userId);
        }
    }
}
