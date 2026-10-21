using Leistd.Email;
using Leistd.Email.Abstractions;
using Leistd.Email.Smtp;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Email.Tests;

/// <summary>发信实现的注册面：发送器单例，宿主先注册的发送器不被任一组件入口覆盖。</summary>
/// <remarks>
/// 两个入口都用 TryAdd：宿主自己接了发信通道（如企业网关）后，再调组件入口也不会被换回空发送器或 SMTP——
/// 被换掉时邮件照常"发送成功"，只是没有发到宿主以为的地方。
/// </remarks>
public sealed class EmailSenderRegistrationTests
{
    [Fact]
    public void Null_sender_is_registered_as_a_singleton()
    {
        var services = new ServiceCollection();

        services.AddNullEmailSender();

        services.AssertSingle<IEmailSender>(ServiceLifetime.Singleton);
        services.AssertImplementedBy<IEmailSender, NullEmailSender>();
    }

    [Fact]
    public void Smtp_sender_is_registered_as_a_singleton()
    {
        var services = new ServiceCollection();

        services.AddSmtpEmailSender(o => o.Host = "127.0.0.1");

        services.AssertSingle<SmtpEmailSender>(ServiceLifetime.Singleton);
        services.AssertSingle<IEmailSender>(ServiceLifetime.Singleton);
    }

    [Fact]
    public void Host_registered_sender_is_kept_by_both_entries()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmailSender, GatewaySender>();

        services.AddNullEmailSender();
        services.AddSmtpEmailSender(o => o.Host = "127.0.0.1");

        services.AssertImplementedBy<IEmailSender, GatewaySender>();
    }

    private sealed class GatewaySender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
