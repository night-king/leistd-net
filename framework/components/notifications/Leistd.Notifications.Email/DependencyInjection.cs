using Leistd.Notifications.Channels;
using Leistd.Notifications.Email.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Leistd.Notifications.Email.Options;

namespace Leistd.Notifications.Email;

/// <summary>通知邮件渠道的注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>登记邮件渠道 <see cref="EmailNotificationChannel"/>：绑定配置节，再应用宿主的编程式配置（代码覆盖配置文件）。</summary>
    /// <remarks>
    /// 宿主还需注册 <c>INotificationRecipientResolver</c>（从自己的用户模型取已验证地址）、一个 <c>IEmailSender</c>
    /// 与后台任务队列（如 <c>AddInProcessBackgroundJobs()</c>）。启用通知偏好时，渠道段取 <see cref="EmailNotificationChannel.ChannelName"/>。
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddEmailNotifications();
    /// builder.Services.AddScoped&lt;INotificationRecipientResolver, UserEmailRecipientResolver&gt;();
    /// </code>
    /// </example>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">编程式配置，在配置节绑定之后应用。</param>
    /// <param name="configSectionPath">配置节路径，默认 <c>Leistd:Notifications:Email</c>；重复调用换用另一配置节时抛出 <see cref="InvalidOperationException"/>。</param>
    public static IServiceCollection AddEmailNotifications(
        this IServiceCollection services,
        Action<EmailNotificationOptions>? configure = null,
        string configSectionPath = EmailNotificationOptions.SectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configSectionPath);

        // 选项只有一份：换用另一配置节的重复调用会让校验消息报错键名
        if (services.Select(descriptor => descriptor.ImplementationInstance).OfType<EmailNotificationOptionsValidator>().FirstOrDefault()
                is { } registered && registered.ConfigSectionPath != configSectionPath)
        {
            throw new InvalidOperationException(
                $"AddEmailNotifications() already binds '{registered.ConfigSectionPath}'; it cannot also bind '{configSectionPath}'.");
        }

        var options = services.AddOptions<EmailNotificationOptions>().BindConfiguration(configSectionPath);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EmailNotificationOptions>>(
            new EmailNotificationOptionsValidator(configSectionPath)));
        options.ValidateOnStart();

        services.TryAddEnumerable(ServiceDescriptor.Scoped<INotificationChannel, EmailNotificationChannel>());
        return services;
    }
}
