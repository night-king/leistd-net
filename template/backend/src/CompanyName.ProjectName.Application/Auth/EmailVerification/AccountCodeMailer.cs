#if (IncludeLocalization)
using System.Globalization;
#endif
#if (IncludeLocalization)
using CompanyName.ProjectName.Application.Settings.Provider;
#endif
using Leistd.Email.Abstractions;
#if (IncludeLocalization)
using Leistd.Settings.Resolution;
#endif
using Leistd.Timing;
using Microsoft.Extensions.Logging;
#if (IncludeLocalization)
using Microsoft.Extensions.Localization;
#endif

namespace CompanyName.ProjectName.Application.Auth.EmailVerification;

/// <summary>绑定账号的验证码邮件是哪一种。</summary>
internal enum AccountCodeMail
{
    /// <summary>找回口令的验证码。</summary>
    PasswordReset,

    /// <summary>登录第二步的验证码。</summary>
    TwoFactorSignIn,

    /// <summary>开启邮箱验证码时的确认码。</summary>
    TwoFactorSetup,

    /// <summary>不带码的告知：这个账号不能自助找回口令，要联系管理员重置。</summary>
    RecoveryNeedsAdministrator
}

/// <summary>一封待发的绑定账号的邮件：只有不可变的投递数据，不带请求期的服务。</summary>
/// <param name="Kind">哪一种邮件，决定文案。</param>
/// <param name="ChallengeId">挑战 Id，用于日志关联（不含码）。</param>
/// <param name="UserId">收件账号。</param>
/// <param name="To">账号当前的已验证邮箱。</param>
/// <param name="Code">验证码明文，只进邮件正文；告知信为 null。</param>
/// <param name="ExpiryMinutes">邮件里写明的有效期。</param>
/// <param name="ExpiresAt">挑战到期时刻（UTC）；排队跨过它就不再发送。</param>
internal sealed record AccountCodeDelivery(
    AccountCodeMail Kind,
    Guid ChallengeId,
    Guid UserId,
    string To,
    string? Code,
    int ExpiryMinutes,
    DateTime ExpiresAt);

/// <summary>在后台发送绑定账号的验证码邮件（找回口令、登录第二步、开启邮箱验证码）：发码请求不等待 SMTP。</summary>
/// <remarks>
/// <para>请求不持锁等待 SMTP：发码时持有的本人凭据锁只保护挑战的签发，慢的邮件服务不会把本人的登录、改口令排在后面。
/// 找回口令另有一层用意：有没有账号，响应耗时一致。</para>
/// <para>投递尽力而为：失败只记日志（挑战 Id、用户 Id、异常类型），不带原始异常消息，也不回报给发码的人——
/// 回报了就等于告诉对方这个邮箱对应一个账号。收不到的人等发送间隔过后重新获取。</para>
/// <para>文案按收件人的界面语言渲染：本人设过语言就用它，否则用组织默认或部署基线。</para>
/// </remarks>
internal sealed class AccountCodeMailer(
    IEmailSender emailSender,
#if (IncludeLocalization)
    ISettingProvider settingProvider,
    IStringLocalizer localizer,
#endif
    IClock clock,
    ILogger<AccountCodeMailer> logger)
{
    public async Task SendAsync(AccountCodeDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.ExpiresAt <= clock.Now)
        {
            // 排队太久，码已过期：发出去也用不了
            logger.LogWarning(
                "{Kind} email {ChallengeId} for user {UserId} expired before it could be sent",
                delivery.Kind,
                delivery.ChallengeId,
                delivery.UserId);
            return;
        }

        try
        {
            var text = await RenderAsync(delivery, cancellationToken);
            await emailSender.SendAsync(
                new EmailMessage
                {
                    To = delivery.To,
                    Subject = text.Subject,
                    Body = delivery.Code is { } code
                        ? VerificationCodeMail.BuildBody(text, code)
                        : VerificationCodeMail.BuildNotice(text)
                },
                cancellationToken);
        }
        // 只有调用方真的取消了才让取消传出去；下游自己的超时、取消也是投递失败，按尽力而为吞掉
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                "Could not send {Kind} email {ChallengeId} to user {UserId}: {ErrorType}",
                delivery.Kind,
                delivery.ChallengeId,
                delivery.UserId,
                ex.GetType().Name);
        }
    }

#if (IncludeLocalization)
    private async Task<VerificationCodeMailText> RenderAsync(AccountCodeDelivery delivery, CancellationToken cancellationToken)
    {
        var language = await settingProvider.GetOrNullForUserAsync(
            SettingConstant.Display.Language, delivery.UserId.ToString(), cancellationToken);

        var previous = CultureInfo.CurrentUICulture;
        try
        {
            if (language is not null)
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            }

            return delivery.Kind switch
            {
                AccountCodeMail.PasswordReset => new VerificationCodeMailText(
                    localizer["PasswordReset:Email:Subject"].Value,
                    localizer["PasswordReset:Email:Intro"].Value,
                    localizer["PasswordReset:Email:Expiry", delivery.ExpiryMinutes].Value,
                    localizer["PasswordReset:Email:Ignore"].Value),
                AccountCodeMail.RecoveryNeedsAdministrator => new VerificationCodeMailText(
                    localizer["PasswordReset:Email:NeedsAdministratorSubject"].Value,
                    localizer["PasswordReset:Email:NeedsAdministrator"].Value,
                    string.Empty,
                    localizer["PasswordReset:Email:Ignore"].Value),
                _ => TwoFactorText(delivery.Kind == AccountCodeMail.TwoFactorSignIn ? "SignIn" : "Setup", delivery.ExpiryMinutes)
            };
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private VerificationCodeMailText TwoFactorText(string kind, int expiryMinutes) => new(
        localizer[$"TwoFactor:Email:{kind}Subject"].Value,
        localizer[$"TwoFactor:Email:{kind}Intro"].Value,
        localizer["TwoFactor:Email:Expiry", expiryMinutes].Value,
        localizer[$"TwoFactor:Email:{kind}Ignore"].Value);
#else
    private static Task<VerificationCodeMailText> RenderAsync(AccountCodeDelivery delivery, CancellationToken cancellationToken)
    {
        var expiry = $"This code will expire in {delivery.ExpiryMinutes} minutes. Please do not share it with anyone.";
        return Task.FromResult(delivery.Kind switch
        {
            AccountCodeMail.PasswordReset => new VerificationCodeMailText(
                "Password Reset Verification Code",
                "You are resetting the password of your account. Your verification code is:",
                expiry,
                "If you did not request this, you can ignore this email. Your password stays unchanged."),
            AccountCodeMail.RecoveryNeedsAdministrator => new VerificationCodeMailText(
                "About resetting your password",
                "Your account signs in with an email verification code, so its password can't be reset by email. Ask your administrator to reset it.",
                string.Empty,
                "If you did not request this, you can ignore this email. Your password stays unchanged."),
            AccountCodeMail.TwoFactorSignIn => new VerificationCodeMailText(
                "Sign-in Verification Code",
                "Someone is signing in to your account. Your verification code is:",
                expiry,
                "If this wasn't you, check how your account signs in and contact your administrator."),
            _ => new VerificationCodeMailText(
                "Confirm Email Verification Codes",
                "You are turning on email verification codes for signing in. Your confirmation code is:",
                expiry,
                "If you did not request this, you can ignore this email. Nothing changes.")
        });
    }
#endif
}
