#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.Errors;
using CompanyName.ProjectName.Api.Auth.Authorization;
using CompanyName.ProjectName.Application.Auth.Sessions;
using Leistd.ExceptionHandling;
using Microsoft.AspNetCore.Authorization;

namespace CompanyName.ProjectName.Api.Middlewares;

/// <summary>受限会话只能调用完成两步验证设置所需的接口。</summary>
/// <remarks>
/// <para>受限与否读本请求的会话状态（<see cref="ICurrentSessionState"/>），由会话 Cookie 校验按最新的策略与账号状态写入，
/// 不看 Cookie 里的声明：组织打开或关闭要求、本人完成设置，下一个请求即生效。Bearer 请求不经 Cookie 校验，不受此限。</para>
#if (OpenIddictServer)
/// <para>匿名可达的端点（带 <see cref="IAllowAnonymous"/>）不在这里拦：<c>/connect/authorize</c> 要让未登录的人开始认证，
/// 由授权端点自己对已登录的受限会话拒绝签发授权码，<c>/connect/logout</c> 照常可用。</para>
#endif
/// <para>按路径与端点元数据统一拦，而不是逐个端点声明：漏标的后果是"受限用户用不了"，而不是"受限用户什么都能做"。</para>
/// <para>拒绝以 403 + <c>Auth:TwoFactorSetupRequired</c> 返回，界面据此把人带去设置页。
/// 页面本身不拦：前端路由守卫负责把受限用户带去设置页，服务端守住的是数据。</para>
/// </remarks>
public sealed class TwoFactorSetupEnforcementMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, ICurrentSessionState session)
    {
        if (session.TwoFactorSetupRequired &&
            IsGuardedPath(context.Request.Path) &&
            context.GetEndpoint() is { } endpoint &&
            endpoint.Metadata.GetMetadata<AllowDuringTwoFactorSetupAttribute>() is null &&
            endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
        {
            // 错误码在不含本地化的形态下也要带：界面按它把人带去设置页
            throw new BusinessException(AuthErrorCodes.TwoFactorSetupRequired, "Set up two-factor authentication before continuing.");
        }

        return next(context);
    }

    // 只管接口、授权端点与实时连接。实时 Hub 只拒新的握手，已建立的连接不中途断开。
    // 页面（SPA 回退、静态文件）照常下发：拦了它们，受限用户连设置页都打不开——
    // 整页刷新任何一个地址都会得到一段 403 JSON，而不是由前端路由守卫把人带去设置页
    private static bool IsGuardedPath(PathString path) =>
        path.StartsWithSegments("/api") || path.StartsWithSegments("/connect") || path.StartsWithSegments("/hubs");
}
#endif
