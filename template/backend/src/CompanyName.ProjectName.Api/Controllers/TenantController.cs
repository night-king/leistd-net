using CompanyName.ProjectName.Application.Shared;
using CompanyName.ProjectName.Application.Permissions.Provider;
#if (Impersonation)
using CompanyName.ProjectName.Application.Tenants.AppServices;
#endif
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CompanyName.ProjectName.Api.Controllers;

/// <summary>模拟登录：以租户管理员身份进入该租户（宿主侧能力）。</summary>
/// <remarks>
/// 租户的查询、创建开通、更新、启停、删除与登录前探测由多租户组件映射在同一前缀下（见 <c>ComponentEndpoints</c>）；
/// 模拟登录依赖本项目的会话与用户模型，留在这里。
/// </remarks>
[Authorize]
[Route("api/v1/tenants")]
public sealed class TenantController(ITenantImpersonationAppService impersonationAppService) : BaseController
{
    /// <summary>以该租户管理员的身份登录（模拟登录）。</summary>
    /// <remarks>
    /// <para>宿主无法跨租户读写：全局过滤器按 <c>TenantId == CurrentTenantId</c> 分区，
    /// 而租户一旦分库更没有跨库查询。要在租户里处理问题，正途是<b>进到那个租户的上下文</b>，
    /// 而不是在宿主界面上关掉过滤器。</para>
    /// <para>会话 Cookie 被整体换成目标租户管理员的主体，并附带发起人声明；
    /// 已认证请求的租户由 cookie claim 定案（请求头改写不了），因此必须重新签发而不是加个头。</para>
    /// </remarks>
    [HttpPost("{id:guid}/impersonate")]
    [Authorize(Policy = PermissionConstant.Tenants.Impersonation)]
    public async Task ImpersonateAsync(Guid id, CancellationToken cancellationToken)
    {
        var principal = await impersonationAppService.ImpersonateAsync(id, cancellationToken);

        await HttpContext.SignInAsync(AuthenticationSchemeNames.SessionCookie, principal,
            new AuthenticationProperties { IsPersistent = true });
    }
}
