#if (LocalIdentity)
using System.Security.Claims;
using CompanyName.ProjectName.Application.Auth.AppServices;
using CompanyName.ProjectName.Application.Auth.Dtos;
using CompanyName.ProjectName.Application.Auth.SignIn;
using CompanyName.ProjectName.Application.Shared;
using Microsoft.AspNetCore.Authentication;

namespace CompanyName.ProjectName.Api.Auth.Sessions;

/// <summary>下发本地会话 Cookie：账号密码登录、第二步验证与外部登录共用同一条签发路径。</summary>
/// <remarks>
/// 新登录先结束当前请求带来的旧会话再签发，避免同一浏览器上旧会话的 Cookie 继续有效；
/// 第一步要求第二步验证时只返回凭据，不签发任何会话。
/// </remarks>
public sealed class SessionCookieIssuer(IUserSessionAppService sessionAppService)
{
    /// <summary>按第一步的结果下发最终会话，或返回第二步凭据。</summary>
    public async Task<SessionLoginOutputDto> CompleteLoginAsync(
        HttpContext httpContext,
        SessionLoginResult result,
        CancellationToken cancellationToken)
    {
        if (result.Principal is null)
        {
            return new SessionLoginOutputDto { RequiresTwoFactor = true, TwoFactorToken = result.TwoFactorToken };
        }

        await SignInNewSessionAsync(httpContext, result.Principal, cancellationToken);
        return new SessionLoginOutputDto();
    }

    /// <summary>结束当前会话后以新主体签发会话（新的一次登录）。</summary>
    public async Task SignInNewSessionAsync(
        HttpContext httpContext,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        await sessionAppService.EndCurrentSessionAsync(cancellationToken);
        await ReissueAsync(httpContext, principal);
    }

    /// <summary>在同一服务端会话上换发 Cookie（主体变化，如结束模拟）。</summary>
    public Task ReissueAsync(HttpContext httpContext, ClaimsPrincipal principal) =>
        httpContext.SignInAsync(AuthenticationSchemeNames.SessionCookie, principal,
            new AuthenticationProperties { IsPersistent = true });
}
#endif
