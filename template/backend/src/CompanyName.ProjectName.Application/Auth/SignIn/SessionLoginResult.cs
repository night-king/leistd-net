#if (LocalIdentity)
using System.Security.Claims;

namespace CompanyName.ProjectName.Application.Auth.SignIn;

/// <summary>登录第一步之后的去向：签发会话，或还要第二步。</summary>
/// <param name="Principal">可以直接签发的会话主体；需要第二步时为 null。</param>
/// <param name="TwoFactorToken">第二步凭据；直接签发时为 null。</param>
public sealed record SessionLoginResult(ClaimsPrincipal? Principal, string? TwoFactorToken)
{
    /// <summary>直接签发会话。</summary>
    public static SessionLoginResult SignedIn(ClaimsPrincipal principal) => new(principal, null);

    /// <summary>还要第二步。</summary>
    public static SessionLoginResult TwoFactorRequired(string token) => new(null, token);
}
#endif
