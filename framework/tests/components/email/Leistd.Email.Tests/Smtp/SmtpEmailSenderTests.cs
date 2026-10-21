using System.Globalization;
using Leistd.Email.Abstractions;
using Leistd.Email.Smtp;
using Leistd.Email.Smtp.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Email.Tests.Smtp;

/// <summary>发送失败必须上抛。</summary>
/// <remarks>调用方依赖发送异常回滚限流槽位与挑战缓存；空 Host 或示例主机名也不能被当成成功跳过。</remarks>
public sealed class SmtpEmailSenderTests
{
    // 保留给 IANA 的丢弃端口，本机上不会有服务监听
    private const int UnreachablePort = 9;

    private static SmtpEmailSender Sender(Action<SmtpOptions>? configure = null)
    {
        var options = new SmtpOptions
        {
            Host = "127.0.0.1",
            Port = UnreachablePort,
            EnableSsl = false,
            DefaultFromAddress = "noreply@example.com",
        };
        configure?.Invoke(options);

        return new SmtpEmailSender(new FixedOptionsMonitor(options), NullLogger<SmtpEmailSender>.Instance);
    }

    private static EmailMessage Message() => new()
    {
        To = "user@example.com",
        Subject = "Account Registration Verification Code",
        Body = "<p>123456</p>",
    };

    [Fact]
    public async Task An_unreachable_host_throws_instead_of_reporting_success()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<Exception>(() => Sender().SendAsync(Message(), cts.Token));
    }

    // 空 Host 不是"静默跳过发送"的信号，必须与其它连接失败一样抛
    [Fact]
    public async Task A_blank_host_throws_rather_than_skipping_the_send()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<Exception>(
            () => Sender(o => o.Host = string.Empty).SendAsync(Message(), cts.Token));
    }

    // 示例主机名不是哨兵值：配置像默认示例也不能假装发出去。
    // 令牌预先取消，发送只要走到连接这一步就抛取消异常，无需解析主机名；
    // 按示例主机名提前静默返回的实现不会抛，用例因此变红。
    [Fact]
    public async Task An_example_host_is_not_treated_as_a_skip_signal()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Sender(o => o.Host = "smtp.example.com").SendAsync(Message(), cts.Token));
    }

    [Fact]
    public async Task A_null_message_is_rejected()
        => await Assert.ThrowsAsync<ArgumentNullException>(() => Sender().SendAsync(null!));

    // 参数每封信取当前值：配置重载后下一封信就用新值，且新值同样过校验，而不是停在进程启动时那份
    [Fact]
    public async Task A_configuration_reload_is_used_and_validated_by_the_next_message()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Leistd:Email:Smtp:Host"] = "127.0.0.1",
                ["Leistd:Email:Smtp:Port"] = UnreachablePort.ToString(CultureInfo.InvariantCulture),
                ["Leistd:Email:Smtp:EnableSsl"] = "false",
                ["Leistd:Email:Smtp:DefaultFromAddress"] = "noreply@example.com",
            })
            .Build();
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddSmtpEmailSender()
            .BuildServiceProvider();
        var sender = provider.GetRequiredService<IEmailSender>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // 重载前参数合规，失败在连接上
        var before = await Assert.ThrowsAnyAsync<Exception>(() => sender.SendAsync(Message(), cts.Token));
        Assert.IsNotType<OptionsValidationException>(before);

        configuration["Leistd:Email:Smtp:DefaultFromAddress"] = "";
        // IOptionsMonitor 在重载回调里就重算并校验，不合规的新值会让 Reload 本身抛出；这里关心的是下一封信
        Assert.IsType<AggregateException>(Record.Exception(configuration.Reload));

        var after = await Assert.ThrowsAsync<OptionsValidationException>(() => sender.SendAsync(Message(), cts.Token));
        Assert.Contains("DefaultFromAddress", after.Message);
    }

    // 固定值的替身：只验发送行为的用例绕开校验与重载
    private sealed class FixedOptionsMonitor(SmtpOptions options) : IOptionsMonitor<SmtpOptions>
    {
        public SmtpOptions CurrentValue => options;

        public SmtpOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<SmtpOptions, string?> listener) => null;
    }
}
