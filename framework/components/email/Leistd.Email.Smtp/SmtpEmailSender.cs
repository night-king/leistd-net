using Leistd.Email.Abstractions;
using Leistd.Redaction;
using Leistd.Email.Smtp.Options;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;

namespace Leistd.Email.Smtp;

/// <summary>经 SMTP 发信的 <see cref="IEmailSender"/>。</summary>
/// <remarks>
/// <para>每次调用建立独立连接，连接、认证与投递异常原样传播，不自动重试。
/// 收到服务器的接受确认之后，断开连接被取消或超时都不改写发送结果；超时记 Warning。</para>
/// <para>每封信取一次 <see cref="IOptionsMonitor{TOptions}.CurrentValue"/>，配置源重载后下一封信即用新值；
/// 新值同样经 <see cref="IValidateOptions{TOptions}"/> 校验，不合规时抛 <see cref="OptionsValidationException"/>。</para>
/// </remarks>
public sealed class SmtpEmailSender(
    IOptionsMonitor<SmtpOptions> options,
    ILogger<SmtpEmailSender> logger) : IEmailSender
{
    // 测试换成调短 Timeout 的真实客户端，不必等满默认两分钟的读超时
    internal Func<SmtpClient> CreateClient { get; init; } = static () => new SmtpClient();

    /// <inheritdoc />
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var current = options.CurrentValue;
        var mime = BuildMessage(message, current);

        using var client = CreateClient();
        await client.ConnectAsync(current.Host, current.Port, ResolveSocketOptions(current), cancellationToken);

        if (!string.IsNullOrWhiteSpace(current.Username))
        {
            // CurrentValue 经 SmtpOptionsValidator 校验，只有用户名、没有口令的配置到不了这里。
            await client.AuthenticateAsync(current.Username, current.Password!, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);

        // 走到这里服务器已接受投递，QUIT 只是善后，它的失败不能把已投递的信报成失败。
        // MailKit 在 QUIT 阶段已吞掉取消、I/O 与协议异常，唯独网络读超时以 TimeoutException 抛出。
        try
        {
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (TimeoutException exception)
        {
            logger.LogWarning(exception, "SMTP QUIT timed out after the message was accepted; the send is still reported as successful");
        }

        // 收件人脱敏后再记（保留域名便于按域名聚合）；主题不脱敏，它是宿主给的文案，组件文档写明不要放个人数据。
        logger.LogInformation(
            "Email sent to {To} with subject {Subject}",
            TextRedactor.RedactEmail(message.To),
            message.Subject);
    }

    // 465 要求连接即 TLS，其余加密端口使用 STARTTLS。
    private static SecureSocketOptions ResolveSocketOptions(SmtpOptions current)
        => current.EnableSsl
            ? current.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls
            : SecureSocketOptions.None;

    internal static MimeMessage BuildMessage(EmailMessage message, SmtpOptions current)
    {
        var mime = new MimeMessage();

        // 自定义发件身份不混用配置中的默认署名。
        var (address, display) = message.FromAddress is { Length: > 0 }
            ? (message.FromAddress, message.FromName)
            : (current.DefaultFromAddress, current.DefaultFromName);

        mime.From.Add(new MailboxAddress(display ?? string.Empty, address));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart(message.IsBodyHtml ? TextFormat.Html : TextFormat.Plain)
        {
            Text = message.Body
        };

        return mime;
    }
}
