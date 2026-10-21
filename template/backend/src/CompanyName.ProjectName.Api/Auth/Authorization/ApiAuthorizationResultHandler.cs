#if (RemoteTokenAuth)
using Leistd.ExceptionHandling;
#endif
using Leistd.OperationRecords.AspNetCore.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace CompanyName.ProjectName.Api.Auth.Authorization;

/// <summary>授权结果里本应用专有的处置：<b>被拒的写操作补一条失败的操作记录</b>。其余一切按默认处理。</summary>
/// <remarks>
/// <para><b>ASP.NET Core 只认一个 <see cref="IAuthorizationMiddlewareResultHandler"/>。</b>框架侧刻意不接管这个扩展点
/// （占住它，宿主唯一的授权处置入口就没了），只提供
/// <c>HttpContext.RecordDeniedOperationAsync()</c> 这个零件，由这里一行调用。</para>
/// <para><b>补记是无条件的。</b><c>[Authorize(Policy = ...)]</c> 的拒绝发生在授权阶段，
/// 请求到不了应用服务，那里的记录调用看不见它——于是"谁在反复尝试他没有的权限"这类问题
/// 没有任何痕迹可查。资源服务形态没有本地账号，但一样有带策略的写端点，
/// 把补记放进本地身份的守卫里会让那半边静默没有审计。</para>
#if (RemoteTokenAuth)
/// <para><b>本地成员被拒带错误码。</b>授权拒绝通常只有 403 状态码；唯独 <see cref="LocalMemberAccessRequirement"/>
/// 的拒绝（本服务停用了该成员、或没有成员行）要让界面区分"没有这项权限"与"整个人在本服务不可用"，
/// 因此转成带 <c>User:*</c> 错误码的业务异常，由全局异常处理按映射写出 403 ProblemDetails。认证失败仍是 401，不经这里。</para>
#endif
/// </remarks>
public sealed class ApiAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler defaultHandler = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden)
        {
            // 记不记由端点上的 [OperationRecordAction] 决定；没打注解就什么都不做。
            await context.RecordDeniedOperationAsync();
#if (RemoteTokenAuth)

            if (authorizeResult.AuthorizationFailure?.FailureReasons
                    .OfType<LocalMemberAccessFailureReason>().FirstOrDefault() is { } reason)
            {
                throw new BusinessException(reason.ErrorCode, reason.Message);
            }
#endif
        }

        await defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }
}
