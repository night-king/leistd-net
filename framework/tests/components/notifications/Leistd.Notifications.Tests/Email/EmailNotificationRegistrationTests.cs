using Leistd.Notifications.AspNetCore.SignalR;
using Leistd.Notifications.AspNetCore.SignalR.Channels;
using Leistd.Notifications.Channels;
using Leistd.Notifications.Email;
using Leistd.Notifications.Email.Channels;
using Leistd.Notifications.Email.Options;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Notifications.Tests.Email;

/// <summary>邮件渠道的注册面：渠道是累加扩展点，按实现去重，与其他渠道共存。</summary>
public sealed class EmailNotificationRegistrationTests
{
    [Fact]
    public void Registration_adds_the_email_channel_as_scoped()
    {
        var services = new ServiceCollection();

        services.AddEmailNotifications();

        var channel = Assert.Single(services, d => d.ServiceType == typeof(INotificationChannel));
        Assert.Equal(typeof(EmailNotificationChannel), channel.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, channel.Lifetime);
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddEmailNotifications());

    // 验证器不计入 AssertIdempotent：登记两份时每条配置错误报两遍
    [Fact]
    public void Repeated_registration_keeps_one_options_validator()
    {
        var services = new ServiceCollection();

        services.AddEmailNotifications().AddEmailNotifications();

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<EmailNotificationOptions>));
    }

    // 渠道按实现去重：加邮件渠道不能挤掉站内推送，顺序也无关
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_email_channel_coexists_with_the_signalr_channel(bool emailFirst)
    {
        var services = new ServiceCollection();
        if (emailFirst)
        {
            services.AddEmailNotifications();
        }

        services.AddNotificationsSignalR();
        if (!emailFirst)
        {
            services.AddEmailNotifications();
        }

        var channels = services.Where(d => d.ServiceType == typeof(INotificationChannel))
            .Select(d => d.ImplementationType)
            .ToArray();
        Assert.Equal(2, channels.Length);
        Assert.Contains(typeof(EmailNotificationChannel), channels);
        Assert.Contains(typeof(SignalRNotificationChannel), channels);
    }

    // 选项只有一份：第二个配置节要么静默叠加绑定，要么让校验消息报错键名，注册时就拒绝
    [Fact]
    public void Repeated_registration_with_another_section_is_rejected()
    {
        var services = new ServiceCollection().AddEmailNotifications();

        var error = Assert.Throws<InvalidOperationException>(
            () => services.AddEmailNotifications(configSectionPath: "Mail:Notifications"));

        Assert.Contains("Mail:Notifications", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Repeated_registration_with_the_same_section_is_accepted()
    {
        var services = new ServiceCollection().AddEmailNotifications(configSectionPath: "Mail:Notifications");

        services.AddEmailNotifications(configSectionPath: "Mail:Notifications");

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<EmailNotificationOptions>));
    }
}
