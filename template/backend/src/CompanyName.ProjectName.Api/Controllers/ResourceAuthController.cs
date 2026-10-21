#if (RemoteTokenAuth)
using CompanyName.ProjectName.Application.Users.AppServices;
using CompanyName.ProjectName.Application.Users.Dtos;
#if (ResourceBrowserSession)
using CompanyName.ProjectName.Application.Shared;
#endif
#if (ResourceBrowserSession)
using Microsoft.AspNetCore.Authentication;
#endif
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CompanyName.ProjectName.Api.Controllers;

[Route("api/v1/auth")]
public sealed class ResourceAuthController(IUserAppService userAppService) : BaseController
{
#if (ResourceBrowserSession)
    [AllowAnonymous]
    [HttpGet("login")]
    public IActionResult Login(string returnUrl = "/workspace") => Challenge(new AuthenticationProperties
    {
        RedirectUri = Url.IsLocalUrl(returnUrl) ? returnUrl : "/workspace"
    }, AuthenticationSchemeNames.OpenIdConnect);

#endif
    /// <summary>当前主体：身份资料取自签发方令牌，超管标记与角色取本服务的授权数据。</summary>
    /// <remarks>签发方令牌里的超管与角色声明属于签发方，不授予本服务任何权限。</remarks>
    [Authorize]
    [HttpGet("me")]
    public Task<CurrentResourceUserOutputDto> GetCurrentUserAsync(CancellationToken cancellationToken)
        => userAppService.GetCurrentResourceUserAsync(cancellationToken);
#if (ResourceBrowserSession)

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> LogoutAsync()
    {
        // 本地会话单独退出且不带回跳地址：Cookie 处理器见到回跳地址就写 302，会盖过官方退出表单（FormPost 不改状态码）
        await HttpContext.SignOutAsync(AuthenticationSchemeNames.SessionCookie);
        return SignOut(new AuthenticationProperties { RedirectUri = "/" }, AuthenticationSchemeNames.OpenIdConnect);
    }
#endif
}
#endif
