using Leistd.Email.Abstractions;
using Leistd.Email.Smtp.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Leistd.Email.Smtp;

/// <summary>SMTP 邮件发送器的注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册 SMTP 邮件发送器：绑定配置节，再应用宿主的编程式配置（代码覆盖配置文件）。</summary>
    /// <remarks>
    /// 可重复调用：服务只注册一次，<paramref name="configure"/> 每次都叠加；已注册其他 <c>IEmailSender</c> 时不覆盖。
    /// 换用另一配置节时两个配置节都会绑定（后绑定的覆盖同名键），校验消息仍按首次调用的配置节给出键名。
    /// </remarks>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">编程式配置，在配置节绑定之后应用。</param>
    /// <param name="configSectionPath">配置节路径，默认 <c>Leistd:Email:Smtp</c>。</param>
    /// <example>
    /// <code>
    /// builder.Services.AddSmtpEmailSender();
    /// </code>
    /// </example>
    public static IServiceCollection AddSmtpEmailSender(
        this IServiceCollection services,
        Action<SmtpOptions>? configure = null,
        string configSectionPath = SmtpOptions.SectionName)
    {
        var options = services.AddOptions<SmtpOptions>().BindConfiguration(configSectionPath);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<SmtpOptions>>(new SmtpOptionsValidator(configSectionPath)));
        options.ValidateOnStart();

        // 接口与实现类型共享同一实例。
        services.TryAddSingleton<SmtpEmailSender>();
        services.TryAddSingleton<IEmailSender>(sp => sp.GetRequiredService<SmtpEmailSender>());

        return services;
    }
}
