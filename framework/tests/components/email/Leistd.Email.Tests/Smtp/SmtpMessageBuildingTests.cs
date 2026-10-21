using Leistd.Email.Abstractions;
using Leistd.Email.Smtp;
using Leistd.Email.Smtp.Options;
using MimeKit;
using Xunit;

namespace Leistd.Email.Tests.Smtp;

/// <summary>MimeMessage 的构造结果。</summary>
/// <remarks>
/// 这一批全是"错了不报错、只让收件人拿到错东西"：从错误身份发出、收到裸 HTML 源码。
/// 断言构造结果而不是连真实服务器——真实 SMTP 能多证明的是连接与认证，那两者失败都以异常收场。
/// </remarks>
public sealed class SmtpMessageBuildingTests
{
    private static SmtpOptions Options() => new()
    {
        Host = "127.0.0.1",
        DefaultFromAddress = "noreply@example.com",
        DefaultFromName = "System Notifications",
    };

    private static EmailMessage Message() => new()
    {
        To = "user@example.com",
        Subject = "Account Registration Verification Code",
        Body = "<p>123456</p>",
    };

    private static MailboxAddress From(MimeMessage mime) => Assert.IsType<MailboxAddress>(Assert.Single(mime.From));

    [Fact]
    public void Recipient_subject_and_body_are_carried_through()
    {
        var mime = SmtpEmailSender.BuildMessage(Message(), Options());

        Assert.Equal("user@example.com", Assert.IsType<MailboxAddress>(Assert.Single(mime.To)).Address);
        Assert.Equal("Account Registration Verification Code", mime.Subject);
        Assert.Equal("<p>123456</p>", mime.TextBody ?? mime.HtmlBody);
    }

    // 发件身份回落：没有它，一封信会从空地址发出（或在 MailboxAddress 上炸），
    // 而调用方从不指定发件人是最常见的用法
    [Fact]
    public void A_message_without_a_sender_falls_back_to_the_configured_default()
    {
        var mime = SmtpEmailSender.BuildMessage(Message(), Options());

        var from = From(mime);
        Assert.Equal("noreply@example.com", from.Address);
        Assert.Equal("System Notifications", from.Name);
    }

    [Fact]
    public void An_explicit_sender_overrides_the_default()
    {
        var message = new EmailMessage
        {
            To = "user@example.com",
            Subject = "s",
            Body = "b",
            FromAddress = "billing@example.com",
            FromName = "Billing",
        };

        var from = From(SmtpEmailSender.BuildMessage(message, Options()));

        Assert.Equal("billing@example.com", from.Address);
        Assert.Equal("Billing", from.Name);
    }

    // 地址与显示名成对：给了地址却没给显示名时不能贴上配置里的默认署名，
    // 否则会发出"自定义地址 + 系统署名"这种没人想要的组合，且没有任何报错
    [Fact]
    public void An_explicit_address_without_a_name_does_not_borrow_the_default_name()
    {
        var message = new EmailMessage
        {
            To = "user@example.com",
            Subject = "s",
            Body = "b",
            FromAddress = "billing@example.com",
        };

        var from = From(SmtpEmailSender.BuildMessage(message, Options()));

        Assert.Equal("billing@example.com", from.Address);
        Assert.Equal(string.Empty, from.Name);
    }

    [Fact]
    public void Html_bodies_are_sent_as_html()
    {
        var mime = SmtpEmailSender.BuildMessage(Message(), Options());

        var body = Assert.IsType<TextPart>(mime.Body);
        Assert.True(body.IsHtml);
        Assert.Equal("<p>123456</p>", mime.HtmlBody);
    }

    // 纯文本被当成 HTML 发出时收件人看到的是转义后的源码，反之看到的是标签本身；
    // 两种都不报错
    [Fact]
    public void Plain_text_bodies_are_sent_as_plain_text()
    {
        var message = new EmailMessage
        {
            To = "user@example.com",
            Subject = "s",
            Body = "Your code is 123456",
            IsBodyHtml = false,
        };

        var body = Assert.IsType<TextPart>(SmtpEmailSender.BuildMessage(message, Options()).Body);

        Assert.True(body.IsPlain);
        Assert.False(body.IsHtml);
    }
}
