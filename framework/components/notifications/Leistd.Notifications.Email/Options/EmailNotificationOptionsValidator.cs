using Microsoft.Extensions.Options;
using Leistd.Notifications.Email.Channels;

namespace Leistd.Notifications.Email.Options;

// 报错里的键名按实际绑定的配置节给出
internal sealed class EmailNotificationOptionsValidator(string sectionPath = EmailNotificationOptions.SectionName)
    : IValidateOptions<EmailNotificationOptions>
{
    // 选项绑定的配置节；重复注册时据此拒绝另一路径。
    public string ConfigSectionPath => sectionPath;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, EmailNotificationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PublicBaseUrl) || EmailNotificationLinks.IsHttpUrl(options.PublicBaseUrl))
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(
            $"{sectionPath}:PublicBaseUrl '{options.PublicBaseUrl}' must be an absolute http(s) URL.");
    }
}
