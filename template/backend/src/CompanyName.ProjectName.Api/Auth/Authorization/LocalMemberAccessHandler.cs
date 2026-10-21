#if (RemoteTokenAuth)
using CompanyName.ProjectName.Application.Users.AppServices;
using CompanyName.ProjectName.Domain.Users.Errors;
using CompanyName.ProjectName.Domain.Users.ValueObjects;
using Leistd.MultiTenancy.Context;
using Leistd.Security.Users;
using Microsoft.AspNetCore.Authorization;

namespace CompanyName.ProjectName.Api.Auth.Authorization;

/// <summary>判定 <see cref="LocalMemberAccessRequirement"/>：先用本请求投影时读到的状态，没有才按主键读一次。</summary>
/// <remarks>
/// <para><b>不重复查库。</b>HTTP 请求在授权之前已由 <c>ResourceUserProvisioningMiddleware</c> 按主键读过这一行，
/// 状态记在 <see cref="LocalMemberAccessSnapshot"/>。快照缺失时（投影失败，或 Hub 方法调用——每次调用是新的作用域，
/// 不经 HTTP 中间件）只做一次只读的主键查询，从不在这里重跑投影的写入路径。</para>
/// <para><b>查库失败不是"被停用"。</b>读状态抛出的异常原样上抛，由全局异常处理答 500；
/// 只有确实读到"没有这一行"或"已停用"才拒绝。</para>
/// <para>Hub 调用里没有 HTTP 上下文：主体与租户来自 SignalR 基座按连接主体建立的环境上下文，
/// 因此这里读 <see cref="ICurrentUser"/> 与 <see cref="ICurrentTenant"/>，不读 <c>HttpContext</c>。</para>
/// </remarks>
public sealed class LocalMemberAccessHandler(
    ICurrentUser currentUser,
    ICurrentTenant currentTenant,
    LocalMemberAccessSnapshot snapshot,
    IUserAppService userAppService) : AuthorizationHandler<LocalMemberAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        LocalMemberAccessRequirement requirement)
    {
        // 未认证由 RequireAuthenticatedUser 答 401，机器主体由自然人断言答 403：都不在这里查库
        if (!currentUser.IsAuthenticated || currentUser.Id is not { } userId)
        {
            return;
        }

        var status = snapshot.TryGet(currentTenant.Id, userId, out var recorded)
            ? recorded
            : await userAppService.GetCurrentUserAccessStatusAsync(
                (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None);

        switch (status)
        {
            case UserAccessStatus.Allowed:
                context.Succeed(requirement);
                break;
            case null:
                context.Fail(new LocalMemberAccessFailureReason(
                    this,
                    UserErrorCodes.LocalMemberMissing,
                    "This account is not yet available in this service. Try again later or contact an administrator."));
                break;
            default:
                context.Fail(new LocalMemberAccessFailureReason(
                    this,
                    UserErrorCodes.LocalAccessDisabled,
                    "This account has been disabled in this service. Contact an administrator."));
                break;
        }
    }
}

/// <summary><see cref="LocalMemberAccessHandler"/> 的拒绝原因：带稳定错误码与可公开的英文文案。</summary>
/// <remarks>由 <see cref="ApiAuthorizationResultHandler"/> 转成带错误码的 403 ProblemDetails；Hub 调用里只导致连接中止。</remarks>
public sealed class LocalMemberAccessFailureReason(
    IAuthorizationHandler handler,
    string errorCode,
    string message) : AuthorizationFailureReason(handler, message)
{
    /// <summary>稳定错误码（<c>User:*</c>）。</summary>
    public string ErrorCode { get; } = errorCode;
}
#endif
