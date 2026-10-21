using CompanyName.ProjectName.Application.ServiceInfo.Dtos;
using Leistd.Security.Clients;
using Leistd.Security.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Leistd.Timing;

namespace CompanyName.ProjectName.Api.Controllers;

/// <summary>
/// 服务信息与调用诊断端点：供其他服务经本服务的 Client 包
/// （CompanyName.ProjectName.Client）探活与联调。
/// </summary>
[Route("api/v1/service-info")]
public sealed class ServiceInfoController(IClock clock) : BaseController
{
    /// <summary>服务基础信息（匿名）：服务名、版本与服务器时间。</summary>
    [AllowAnonymous]
    [HttpGet]
    public ServiceInfoOutputDto Get()
    {
        var assemblyName = typeof(ServiceInfoController).Assembly.GetName();
        return new ServiceInfoOutputDto(
            assemblyName.Name ?? "unknown",
            assemblyName.Version?.ToString() ?? "unknown",
            clock.Now);
    }

    /// <summary>
    /// 返回「本次调用以谁的身份进入」：已认证用户与调用方客户端。
    /// 默认授权策略要求可用的自然人用户，资源服务间调用通过 Token Exchange 令牌证明用户；
    /// 面向纯工作负载（无用户上下文）的端点应单独声明自己的策略。
    /// </summary>
    [Authorize]
    [HttpGet("whoami")]
    public WhoAmIOutputDto WhoAmI(
        [FromServices] ICurrentUser currentUser,
        [FromServices] ICurrentClient currentClient) =>
        new(currentUser.Id, currentUser.Username, currentClient.ClientId);
}
