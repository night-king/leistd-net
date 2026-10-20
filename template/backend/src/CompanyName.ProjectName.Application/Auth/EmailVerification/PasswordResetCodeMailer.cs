#if (IncludeLocalization)
using System.Globalization;
#endif
using System.Net;
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

/// <summary>一封待发的找回口令验证码邮件：只有不可变的投递数据，不带请求期的服务。</summary>
/// <param name="ChallengeId">挑战 Id，用于日志关联（不含码）。</param>
/// <param name="UserId">收件账号。</param>
/// <param name="To">账号当前的已验证邮箱。</param>
/// <param name="Code">验证码明文，只进邮件正文。</param>
/// <param name="ExpiryMinutes">邮件里写明的有效期。</param>
/// <param name="ExpiresAt">挑战到期时刻（UTC）；排队跨过它就不再发送。</param>
internal sealed record PasswordResetCodeDelivery(
    Guid ChallengeId,
    Guid UserId,
    string To,
    string Code,
    int ExpiryMinutes,
    DateTime ExpiresAt);

/// <summary>在后台发送找回口令的验证码：发码请求不等待 SMTP，有没有账号的响应耗时一致。</summary>
/// <remarks>
/// 投递尽力而为：失败只记日志（挑战 Id、用户 Id、异常类型），不带原始异常消息，也不回报给发码的人——
/// 回报了就等于告诉对方这个邮箱对应一个账号。收不到的人等发送间隔过后重新获取。
/// </remarks>
internal sealed class PasswordResetCodeMailer(
    IEmailSender emailSender,
#if (IncludeLocalization)
    ISettingProvider settingProvider,
    IStringLocalizer localizer,
#endif
    IClock clock,
    ILogger<PasswordResetCodeMailer> logger)
{
    public async Task SendAsync(PasswordResetCodeDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.ExpiresAt <= clock.Now)
        {
            // 排队太久，码已过期：发出去也用不了
            logger.LogWarning(
                "Password reset code {ChallengeId} for user {UserId} expired before it could be sent",
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
                    Body = BuildBody(text, delivery.Code)
                },
                cancellationToken);
        }
        // 只有调用方真的取消了才让取消传出去；下游自己的超时、取消也是投递失败，按尽力而为吞掉
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                "Could not send password reset code {ChallengeId} to user {UserId}: {ErrorType}",
                delivery.ChallengeId,
                delivery.UserId,
                ex.GetType().Name);
        }
    }

    private sealed record MailText(string Subject, string Intro, string Expiry, string Ignore);

#if (IncludeLocalization)
    // 文案按收件人的界面语言渲染：本人设过语言就用它，否则用组织默认或部署基线
    private async Task<MailText> RenderAsync(PasswordResetCodeDelivery delivery, CancellationToken cancellationToken)
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

            return new MailText(
                localizer["PasswordReset:Email:Subject"].Value,
                localizer["PasswordReset:Email:Intro"].Value,
                localizer["PasswordReset:Email:Expiry", delivery.ExpiryMinutes].Value,
                localizer["PasswordReset:Email:Ignore"].Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
#else
    private static Task<MailText> RenderAsync(PasswordResetCodeDelivery delivery, CancellationToken cancellationToken) =>
        Task.FromResult(new MailText(
            "Password Reset Verification Code",
            "You are resetting the password of your account. Your verification code is:",
            $"This code will expire in {delivery.ExpiryMinutes} minutes. Please do not share it with anyone.",
            "If you did not request this, you can ignore this email. Your password stays unchanged."));
#endif

    private static string BuildBody(MailText text, string code) => $@"
<div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; border: 1px solid #e2e8f0; border-radius: 8px; overflow: hidden;'>
    <div style='background-color: #0f172a; padding: 20px; text-align: center; color: white;'>
        <h2 style='margin: 0;'>{WebUtility.HtmlEncode(text.Subject)}</h2>
    </div>
    <div style='padding: 30px; background-color: #f8fafc; color: #334155;'>
        <p>{WebUtility.HtmlEncode(text.Intro)}</p>
        <div style='margin: 20px 0; text-align: center;'>
            <span style='font-size: 32px; font-weight: bold; letter-spacing: 8px; color: #2563eb;'>{WebUtility.HtmlEncode(code)}</span>
        </div>
        <p style='font-size: 14px; color: #64748b;'>{WebUtility.HtmlEncode(text.Expiry)}</p>
        <p style='font-size: 14px; color: #64748b; margin-top: 30px;'>{WebUtility.HtmlEncode(text.Ignore)}</p>
    </div>
</div>";
}
