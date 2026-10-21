using Leistd.Email.Abstractions;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Leistd.Email.Tests.Core;

/// <summary>空发送器的行为与它的告警级别。</summary>
/// <remarks>
/// 级别是这批用例的重点：它是"本环境不会真的发信"的唯一信号。降级成 Debug 之后，
/// 一个误把它注册进生产的部署会完全静默地丢掉每一封信。
/// </remarks>
public sealed class NullEmailSenderTests
{
    private static (NullEmailSender Sender, FakeLogCollector Logs) Create()
    {
        var collector = new FakeLogCollector();
        var logger = new FakeLogger<NullEmailSender>(collector);
        return (new NullEmailSender(logger), collector);
    }

    private static EmailMessage Message() => new()
    {
        To = "user@example.com",
        Subject = "Account Registration Verification Code",
        Body = "<p>123456</p>",
    };

    [Fact]
    public async Task Sending_succeeds_without_delivering()
    {
        var (sender, _) = Create();

        await sender.SendAsync(Message());
    }

    [Fact]
    public async Task Sending_logs_at_warning_level()
    {
        var (sender, logs) = Create();

        await sender.SendAsync(Message());

        var record = Assert.Single(logs.GetSnapshot());
        Assert.Equal(LogLevel.Warning, record.Level);
    }

    // 收件人与主题必须进日志：开发环境靠它确认"这封信本该发给谁"。
    // 但收件人只能是脱敏形态——本类是给"没配 SMTP"兜底的，一旦被误注册到生产，
    // 日志里就会留下每一个收件人。把 TextRedactor 去掉，这条会红。
    [Fact]
    public async Task The_log_carries_a_redacted_recipient_and_the_subject()
    {
        var (sender, logs) = Create();

        await sender.SendAsync(Message());

        var message = Assert.Single(logs.GetSnapshot()).Message;
        Assert.Contains("us***@example.com", message, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", message, StringComparison.Ordinal);
        Assert.Contains("Account Registration Verification Code", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_null_message_is_rejected()
    {
        var (sender, _) = Create();

        await Assert.ThrowsAsync<ArgumentNullException>(() => sender.SendAsync(null!));
    }

    [Fact]
    public void Registration_resolves_the_null_sender()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddNullEmailSender();

        services.AssertResolvesTo<IEmailSender, NullEmailSender>();
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services =>
        {
            services.AddLogging();
            services.AddNullEmailSender();
        });
}
