#if (LocalIdentity)
using Leistd.Security.OneTimeCodes.VerificationCodes;
using CompanyName.ProjectName.Application.Settings.Errors;
using CompanyName.ProjectName.Application.Settings.Provider;
using Leistd.ExceptionHandling;
using Leistd.Settings.Validation;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Net.Mail;

namespace CompanyName.ProjectName.Application.Settings.Validators;

/// <summary>邮件相关设置的业务取值：开启邮箱验证的运行前提、发件地址的写法。</summary>
internal sealed class EmailSettingValidator(
    IOptions<VerificationCodeOptions> verificationCodeOptions,
    ILogger<EmailSettingValidator> logger) : ISettingValueValidator
{
    public Task ValidateAsync(SettingValueValidationContext context, CancellationToken cancellationToken = default)
    {
        switch (context.Definition.Name)
        {
            // 开启前必须确认部署已经给了可用的摘要密钥。密钥只由配置/密钥设施提供、不进设置表；
            // 启动校验只看配置里的布尔值，所以默认部署（关闭邮箱验证、未配密钥）能正常启动——
            // 不在这里拦，管理员随后在设置页打开，直到真的发码时才在摘要计算处抛 500。
            case SettingConstant.Registration.EnableEmailVerification
                when context.Value == "true" && !verificationCodeOptions.Value.IsKeyUsable:
                logger.LogWarning(
                    "Email verification cannot be enabled: {Section}:Key is missing or shorter than {MinimumKeyBytes} bytes.",
                    VerificationCodeOptions.SectionName, VerificationCodeOptions.MinimumKeyBytes);
                throw new BusinessException(AppSettingErrorCodes.EmailVerificationKeyMissing,
                    "Email verification cannot be enabled in the current deployment. Please contact your administrator.");

            // 只收裸地址：带显示名的写法（"Acme <a@b.c>"）在这里放行，发信时才因为解析不出而失败
            case SettingConstant.Email.DefaultFromAddress
                when !MailAddress.TryCreate(context.Value, out var address) || address.Address != context.Value:
                throw new BusinessException(AppSettingErrorCodes.EmailAddressInvalid, "The sender address is not a valid email address.")
                    .WithData("Value", context.Value);
        }

        return Task.CompletedTask;
    }
}
#endif
