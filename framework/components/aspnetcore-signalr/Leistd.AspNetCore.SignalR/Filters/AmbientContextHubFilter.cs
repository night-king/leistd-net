using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Leistd.AmbientContext;
using Leistd.AspNetCore.SignalR.Options;
using Leistd.Security.Claims;

namespace Leistd.AspNetCore.SignalR.Filters;

/// <summary>为每次 Hub 调用建立环境上下文，并复评宿主授权策略。</summary>
/// <remarks>
/// Hub 方法调用不经过 HTTP 中间件。本过滤器基于握手主体建立调用上下文，
/// 并按 <see cref="HubIdentityOptions.PolicyName"/> 和复评间隔检查身份有效性；对全部 Hub 生效。
/// 复评不通过时中止连接、不执行方法、只记 Information，不抛异常，客户端以连接关闭结束这次调用。
/// </remarks>
public sealed class AmbientContextHubFilter(
    IOptions<HubIdentityOptions> options,
    ILogger<AmbientContextHubFilter> logger,
    TimeProvider? timeProvider = null) : IHubFilter
{
    // 通过注入时间源计算身份复评间隔。
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    // 匿名连接也建立上下文，但只对 IsAuthenticated 主体复评；非 null 不代表已认证。
    private static ClaimsPrincipal Principal(HubCallerContext context) => context.User ?? new ClaimsPrincipal();

    private static bool IsAuthenticated(ClaimsPrincipal principal) => principal.HasAuthenticatedIdentity();

    private const string LastRevalidatedKey = "Leistd.AspNetCore.SignalR.LastRevalidated";

    /// <inheritdoc />
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var principal = Principal(invocationContext.Context);

        // 先建立上下文再复评，使授权处理器可读取当前用户等环境态。
        using (Begin(invocationContext.ServiceProvider, principal))
        {
            if (IsAuthenticated(principal) && !await RevalidateOrAbortAsync(
                    invocationContext.ServiceProvider,
                    invocationContext.Context,
                    principal,
                    invocationContext.HubMethodName))
            {
                // 连接已中止，方法不执行。不抛异常：SignalR 会把方法与过滤器抛出的任何异常（含 HubException）
                // 按 Error 记一条调用失败，而这是预期结局；连接已中止，拒绝消息也送不到客户端。
                // 返回值不会写回客户端，流式调用拿到 null 时官方只记 Debug。
                return null;
            }

            return await next(invocationContext);
        }
    }

    /// <inheritdoc />
    /// <remarks>连接建立同样在环境上下文内执行（<c>OnConnectedAsync</c> 里的加组等动作需要租户与主体）。</remarks>
    public async Task OnConnectedAsync(
        HubLifetimeContext context,
        Func<HubLifetimeContext, Task> next)
    {
        using (Begin(context.ServiceProvider, Principal(context.Context)))
        {
            await next(context);
        }
    }

    /// <inheritdoc />
    public Task OnDisconnectedAsync(
        HubLifetimeContext context,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        // 断开清理同样需要上下文，否则清理会落到宿主分区。
        return DisconnectAsync(context, exception, next, Principal(context.Context));
    }

    private async Task DisconnectAsync(
        HubLifetimeContext context,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next,
        System.Security.Claims.ClaimsPrincipal principal)
    {
        using (Begin(context.ServiceProvider, principal))
        {
            await next(context, exception);
        }
    }

    private static IDisposable Begin(
        IServiceProvider serviceProvider,
        System.Security.Claims.ClaimsPrincipal principal)
        => serviceProvider.GetRequiredService<IAmbientContext>().Begin(principal);

    // 账号有效性复评：不通过即中止连接并返回 false，客户端需重新建立连接并重新认证
    private async Task<bool> RevalidateOrAbortAsync(
        IServiceProvider serviceProvider,
        HubCallerContext callerContext,
        System.Security.Claims.ClaimsPrincipal principal,
        string hubMethodName)
    {
        var interval = options.Value.RevalidationInterval;
        if (interval.HasValue &&
            callerContext.Items.TryGetValue(LastRevalidatedKey, out var last) &&
            last is DateTimeOffset lastAt &&
            _timeProvider.GetUtcNow() - lastAt < interval.Value)
        {
            return true;
        }

        var authorization = serviceProvider.GetService<IAuthorizationService>();
        if (authorization is null)
        {
            // 宿主没装授权：没有策略可复评，跳过。缺失是宿主的选择，不是本过滤器的默认。
            return true;
        }

        var result = options.Value.PolicyName is { } policyName
            ? await authorization.AuthorizeAsync(principal, resource: null, policyName)
            : await AuthorizeWithDefaultPolicyAsync(serviceProvider, authorization, principal);

        callerContext.Items[LastRevalidatedKey] = _timeProvider.GetUtcNow();

        if (result.Succeeded)
        {
            return true;
        }

        logger.LogInformation(
            "Aborting hub connection {ConnectionId}: the principal no longer satisfies the policy (method {HubMethod})",
            callerContext.ConnectionId, hubMethodName);

        callerContext.Abort();
        return false;
    }

    private static async Task<AuthorizationResult> AuthorizeWithDefaultPolicyAsync(
        IServiceProvider serviceProvider,
        IAuthorizationService authorization,
        System.Security.Claims.ClaimsPrincipal principal)
    {
        var policyProvider = serviceProvider.GetService<IAuthorizationPolicyProvider>();
        if (policyProvider is null)
        {
            return AuthorizationResult.Success();
        }

        var policy = await policyProvider.GetDefaultPolicyAsync();
        return await authorization.AuthorizeAsync(principal, resource: null, policy.Requirements);
    }
}
