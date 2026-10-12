using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Leistd.AmbientContext;
using Leistd.Security.Claims;

namespace Leistd.AspNetCore.SignalR.Tests.TestDoubles;

internal sealed class TestHub : Hub
{
    public void Ping()
    {
    }
}

// 无 HTTP 上下文的主体访问器：Hub 调用里没有 HttpContext，
// 只有过滤器经 Change() 显式建立的主体。

// 记录建立顺序与当时能否读到主体，用于钉住"复评在上下文之内"。
internal sealed class RecordingContributor(ICurrentPrincipalAccessor accessor) : IAmbientContextContributor
{
    public int Entered { get; private set; }

    public IDisposable? Enter(AmbientContextEnterContext context)
    {
        Entered++;
        // 主体必须先于其它维度建立
        if (accessor.Principal is null)
        {
            throw new InvalidOperationException("The principal should already be established.");
        }

        return null;
    }
}

internal sealed class RequirePrincipalRequirement : IAuthorizationRequirement;

// 刻意读环境态而不是 AuthorizationHandlerContext.User——
// 宿主为 HTTP 路径写的 handler 就是这个写法。
internal sealed class RequirePrincipalHandler(ICurrentPrincipalAccessor accessor)
    : AuthorizationHandler<RequirePrincipalRequirement>
{
    public int Invocations { get; private set; }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        RequirePrincipalRequirement requirement)
    {
        Invocations++;

        if (accessor.Principal?.Identity?.IsAuthenticated == true)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal sealed class DenyRequirement : IAuthorizationRequirement;

// 恒不 Succeed：用来表达"有身份但策略不通过"，与匿名路径区分开。
internal sealed class DenyHandler : AuthorizationHandler<DenyRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        DenyRequirement requirement) => Task.CompletedTask;
}
