#if (LocalIdentity)
using System.Security.Claims;
using CompanyName.ProjectName.Domain.Users.ValueObjects;

namespace CompanyName.ProjectName.Application.Auth.SignIn;

/// <summary>登录第一步之后的去向：签发会话，或还要第二步。</summary>
/// <param name="Principal">可以直接签发的会话主体；需要第二步时为 null。</param>
/// <param name="TwoFactor">第二步的凭据与可用方式；直接签发时为 null。</param>
public sealed record SessionLoginResult(ClaimsPrincipal? Principal, TwoFactorPrompt? TwoFactor)
{
    /// <summary>直接签发会话。</summary>
    public static SessionLoginResult SignedIn(ClaimsPrincipal principal) => new(principal, null);

    /// <summary>还要第二步。</summary>
    public static SessionLoginResult TwoFactorRequired(TwoFactorPrompt prompt) => new(null, prompt);
}

/// <summary>还要第二步时交给浏览器的内容。</summary>
/// <param name="Token">第二步凭据，几分钟内有效。</param>
/// <param name="Methods">本人此刻能用的方式，按系统顺序，恢复码在最后。</param>
/// <param name="DefaultMethod">直接给出的方式。</param>
/// <param name="MaskedEmail">邮箱验证码能用时，收码地址的打码形式。</param>
public sealed record TwoFactorPrompt(
    string Token,
    IReadOnlyList<TwoFactorMethod> Methods,
    TwoFactorMethod DefaultMethod,
    string? MaskedEmail);
#endif
