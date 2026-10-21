using Leistd.Email.Abstractions;
using Leistd.Email.Smtp;
using Leistd.Email.Smtp.Options;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Email.Tests.Smtp;

/// <summary>启动期配置校验。</summary>
/// <remarks>
/// 这几条留到运行期才发现，代价是"用户已经点了发送"：
/// 注册验证码流程会把一个永远收不到码的挑战交给用户，而日志里只有一次发送失败。
/// </remarks>
public sealed class SmtpOptionsValidatorTests
{
    private static SmtpOptions Valid() => new()
    {
        Host = "127.0.0.1",
        Port = 587,
        DefaultFromAddress = "noreply@example.com",
    };

    private static ValidateOptionsResult Validate(Action<SmtpOptions> mutate)
    {
        var options = Valid();
        mutate(options);

        // 经容器取校验器，同时钉住"AddSmtpEmailSender 真的把它登记上了"
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSmtpEmailSender();
        using var provider = services.BuildServiceProvider();

        var validator = Assert.Single(provider.GetServices<IValidateOptions<SmtpOptions>>());
        return validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    [Fact]
    public void A_fully_specified_configuration_passes()
        => Assert.True(Validate(_ => { }).Succeeded);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Host_is_required(string host)
        => Assert.Contains("Host is required", Validate(o => o.Host = host).FailureMessage);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Port_must_be_in_range(int port)
        => Assert.Contains("Port must be between", Validate(o => o.Port = port).FailureMessage);

    [Fact]
    public void Default_from_address_is_required()
        => Assert.Contains("DefaultFromAddress is required", Validate(o => o.DefaultFromAddress = "").FailureMessage);

    // 前三个是完整邮箱形态：MailboxAddress.TryParse 会放行它们，而发送路径用的
    // MailboxAddress(name, address) 只接受 addr-spec——校验一旦用了前者，这类取值就能
    // 通过启动校验，然后让每一封走默认发件人的信在构造阶段抛。后两个是纯粹解析不了的取值。
    [Theory]
    [InlineData("System <noreply@example.com>")]
    [InlineData("\"System Notifications\" <noreply@example.com>")]
    [InlineData("<noreply@example.com>")]
    [InlineData("not an address")]
    [InlineData("a@b@c")]
    public void An_address_the_send_path_cannot_use_is_rejected_at_startup(string address)
    {
        var failure = Validate(o => o.DefaultFromAddress = address).FailureMessage;

        Assert.Contains("is not a bare mailbox address", failure, StringComparison.Ordinal);
        Assert.Contains("DefaultFromName", failure, StringComparison.Ordinal);
    }

    // 校验与发送必须用同一套判定：凡是校验放行的取值，构造消息都不能抛。
    [Theory]
    [InlineData("noreply@example.com")]
    [InlineData("noreply")]                     // 不带域名：可投递性是 SMTP 层的事，不在这道闸门
    [InlineData("first.last+tag@sub.example.com")]
    public void Anything_the_validator_accepts_can_be_used_to_build_a_message(string address)
    {
        var options = Valid();
        options.DefaultFromAddress = address;
        Assert.True(Validate(o => o.DefaultFromAddress = address).Succeeded);

        var mime = SmtpEmailSender.BuildMessage(
            new EmailMessage { To = "user@example.com", Subject = "s", Body = "b" }, options);

        Assert.Equal(address, Assert.IsType<MailboxAddress>(Assert.Single(mime.From)).Address);
    }

    // 只给一半凭据时发送方会跳过认证，而配置看上去是配好的
    [Fact]
    public void Username_without_password_is_rejected()
        => Assert.Contains("must be set together", Validate(o => o.Username = "mailer").FailureMessage);

    [Fact]
    public void Password_without_username_is_rejected()
        => Assert.Contains("must be set together", Validate(o => o.Password = "s3cret").FailureMessage);

    [Fact]
    public void Both_credentials_together_pass()
        => Assert.True(Validate(o => { o.Username = "mailer"; o.Password = "s3cret"; }).Succeeded);

    [Fact]
    public void Anonymous_delivery_passes()
        => Assert.True(Validate(o => { o.Username = null; o.Password = null; }).Succeeded);

    // 失败消息必须带上配置节名：宿主启动失败时，这是唯一指向"改哪里"的线索
    [Fact]
    public void Failure_message_names_the_configuration_section()
        => Assert.StartsWith(SmtpOptions.SectionName, Validate(o => o.Host = "").FailureMessage);
}
