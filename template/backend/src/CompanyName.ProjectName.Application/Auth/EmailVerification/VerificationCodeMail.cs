using System.Net;

namespace CompanyName.ProjectName.Application.Auth.EmailVerification;

/// <summary>验证码邮件的文案：标题、引导语、有效期说明与"不是本人操作"时的说明。</summary>
internal sealed record VerificationCodeMailText(string Subject, string Intro, string Expiry, string Ignore);

/// <summary>验证码邮件的正文（找回口令与两步验证共用同一版式），文案与验证码都经 HTML 编码。</summary>
internal static class VerificationCodeMail
{
    public static string BuildBody(VerificationCodeMailText text, string code) => $@"
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

    /// <summary>不带码的告知信：同一版式，去掉验证码与有效期。</summary>
    public static string BuildNotice(VerificationCodeMailText text) => $@"
<div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; border: 1px solid #e2e8f0; border-radius: 8px; overflow: hidden;'>
    <div style='background-color: #0f172a; padding: 20px; text-align: center; color: white;'>
        <h2 style='margin: 0;'>{WebUtility.HtmlEncode(text.Subject)}</h2>
    </div>
    <div style='padding: 30px; background-color: #f8fafc; color: #334155;'>
        <p>{WebUtility.HtmlEncode(text.Intro)}</p>
        <p style='font-size: 14px; color: #64748b; margin-top: 30px;'>{WebUtility.HtmlEncode(text.Ignore)}</p>
    </div>
</div>";
}
