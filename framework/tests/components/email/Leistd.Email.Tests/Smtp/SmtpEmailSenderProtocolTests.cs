using Leistd.Email.Abstractions;
using Leistd.Email.Smtp;
using Leistd.Email.Smtp.Options;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Email.Tests.Smtp;

/// <summary>以服务器接受投递为界：之前的失败上抛，之后断开阶段的取消或超时不改写发送结果。</summary>
/// <remarks>经本机 SMTP 应答驱动真实 MailKit；调用方据异常回滚验证码挑战与发送配额，已投递的信被报成失败会让用户收到用不了的码。</remarks>
public sealed class SmtpEmailSenderProtocolTests
{
    private readonly FakeLogger<SmtpEmailSender> _logger = new();

    private SmtpEmailSender Sender(LoopbackSmtpServer server, int timeoutMilliseconds = 10_000)
        => new(new FixedOptionsMonitor(new SmtpOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            EnableSsl = false,
            DefaultFromAddress = "noreply@example.com",
        }), _logger)
        {
            CreateClient = () => new SmtpClient { Timeout = timeoutMilliseconds },
        };

    private static EmailMessage Message() => new()
    {
        To = "user@example.com",
        Subject = "Verification Code",
        Body = "<p>123456</p>",
    };

    [Fact]
    public async Task An_accepted_message_is_sent()
    {
        await using var server = new LoopbackSmtpServer();

        await Sender(server).SendAsync(Message());

        Assert.True(server.Accepted);
    }

    [Fact]
    public async Task Cancellation_during_quit_after_acceptance_still_reports_success()
    {
        using var request = new CancellationTokenSource();
        await using var server = new LoopbackSmtpServer(onQuit: request.Cancel, replyToQuit: false);

        await Sender(server).SendAsync(Message(), request.Token);

        Assert.True(server.Accepted);
        Assert.True(request.IsCancellationRequested);
    }

    [Fact]
    public async Task A_quit_timeout_after_acceptance_still_reports_success_and_is_logged()
    {
        await using var server = new LoopbackSmtpServer(replyToQuit: false);

        await Sender(server, timeoutMilliseconds: 200).SendAsync(Message());

        Assert.True(server.Accepted);
        var warning = Assert.Single(_logger.Collector.GetSnapshot(), r => r.Level == LogLevel.Warning);
        Assert.IsType<TimeoutException>(warning.Exception);
    }

    [Fact]
    public async Task A_rejected_message_throws()
    {
        await using var server = new LoopbackSmtpServer(dataReply: "554 5.7.1 rejected");

        await Assert.ThrowsAsync<SmtpCommandException>(() => Sender(server).SendAsync(Message()));
        Assert.False(server.Accepted);
    }

    [Fact]
    public async Task Cancellation_before_acceptance_throws()
    {
        using var request = new CancellationTokenSource();
        await using var server = new LoopbackSmtpServer(dataReply: null, onDataEnd: request.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sender(server).SendAsync(Message(), request.Token));
        Assert.False(server.Accepted);
    }

    [Fact]
    public async Task A_timeout_before_acceptance_throws()
    {
        await using var server = new LoopbackSmtpServer(dataReply: null);

        await Assert.ThrowsAsync<TimeoutException>(
            () => Sender(server, timeoutMilliseconds: 200).SendAsync(Message()));
        Assert.False(server.Accepted);
    }

    private sealed class FixedOptionsMonitor(SmtpOptions options) : IOptionsMonitor<SmtpOptions>
    {
        public SmtpOptions CurrentValue => options;

        public SmtpOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<SmtpOptions, string?> listener) => null;
    }
}
