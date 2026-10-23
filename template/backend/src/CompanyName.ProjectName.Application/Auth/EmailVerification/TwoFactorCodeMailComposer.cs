#if (IncludeLocalization)
using System.Globalization;
using CompanyName.ProjectName.Application.Settings.Provider;
#endif
using Leistd.Email.Abstractions;
#if (IncludeLocalization)
using Leistd.Settings.Resolution;
using Microsoft.Extensions.Localization;
#endif

namespace CompanyName.ProjectName.Application.Auth.EmailVerification;

/// <summary>两步验证的验证码邮件：登录第二步的码，以及开启邮箱验证码时的确认码。</summary>
/// <remarks>文案按收件人的界面语言渲染，与找回口令的邮件同一做法；正文版式见 <see cref="VerificationCodeMail"/>。</remarks>
internal sealed class TwoFactorCodeMailComposer
#if (IncludeLocalization)
    (ISettingProvider settingProvider, IStringLocalizer localizer)
#endif
{
    /// <summary>登录第二步的验证码邮件。</summary>
    public EmailCodeComposer ForSignIn(EmailCodeRecipient recipient) => Compose(recipient, "SignIn");

    /// <summary>开启邮箱验证码时的确认码邮件。</summary>
    public EmailCodeComposer ForSetup(EmailCodeRecipient recipient) => Compose(recipient, "Setup");

    private EmailCodeComposer Compose(EmailCodeRecipient recipient, string kind) =>
        async (code, expiryMinutes, cancellationToken) =>
        {
            var text = await RenderAsync(recipient.UserId, kind, expiryMinutes, cancellationToken);
            return new EmailMessage
            {
                To = recipient.Email,
                Subject = text.Subject,
                Body = VerificationCodeMail.BuildBody(text, code)
            };
        };

#if (IncludeLocalization)
    private async Task<VerificationCodeMailText> RenderAsync(
        Guid userId, string kind, int expiryMinutes, CancellationToken cancellationToken)
    {
        var language = await settingProvider.GetOrNullForUserAsync(
            SettingConstant.Display.Language, userId.ToString(), cancellationToken);

        var previous = CultureInfo.CurrentUICulture;
        try
        {
            if (language is not null)
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            }

            return new VerificationCodeMailText(
                localizer[$"TwoFactor:Email:{kind}Subject"].Value,
                localizer[$"TwoFactor:Email:{kind}Intro"].Value,
                localizer["TwoFactor:Email:Expiry", expiryMinutes].Value,
                localizer[$"TwoFactor:Email:{kind}Ignore"].Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
#else
    private static Task<VerificationCodeMailText> RenderAsync(
        Guid userId, string kind, int expiryMinutes, CancellationToken cancellationToken) =>
        Task.FromResult(kind == "SignIn"
            ? new VerificationCodeMailText(
                "Sign-in Verification Code",
                "Someone is signing in to your account. Your verification code is:",
                $"This code will expire in {expiryMinutes} minutes. Please do not share it with anyone.",
                "If this wasn't you, check how your account signs in and contact your administrator.")
            : new VerificationCodeMailText(
                "Confirm Email Verification Codes",
                "You are turning on email verification codes for signing in. Your confirmation code is:",
                $"This code will expire in {expiryMinutes} minutes. Please do not share it with anyone.",
                "If you did not request this, you can ignore this email. Nothing changes."));
#endif
}
