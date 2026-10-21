using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Leistd.Security.Users;
using Leistd.RealTime.Subscriptions;

namespace Leistd.RealTime.AspNetCore.SignalR.Hubs;

/// <summary>实时业务事件 Hub：客户端订阅或取消订阅资源，资源变更时收到推送。</summary>
/// <remarks>
/// 授权与组操作传递 <see cref="HubCallerContext.ConnectionAborted"/>；连接已中止时出现的 <see cref="OperationCanceledException"/>
/// 只记 Debug 并正常返回，连接仍在时的取消（如背板确认超时）与其他异常照常抛出。
/// 授权拒绝是预期结局，以返回值告知调用方并记 Warning，不抛异常：SignalR 会把 Hub 方法抛出的任何异常
/// （含 <see cref="HubException"/>）按 Error 记一条调用失败。
/// 背板不一定响应该令牌，组操作的等待不保证随断线立即结束。
/// </remarks>
public class RealTimeHub(
    ICurrentUser currentUser,
    IRealTimeSubscriptionAuthorizer subscriptionAuthorizer,
    ILogger<RealTimeHub> logger) : Hub
{
    /// <summary>订阅资源变更。</summary>
    /// <remarks>授权器无条件参与判定；公共资源场景显式注册 <c>AllowAllRealTimeSubscriptionAuthorizer</c>。</remarks>
    /// <param name="resourceKey">资源标识（如 "product-profile:{id}"）。</param>
    /// <returns>
    /// 授权通过且已加入资源组时为 <see langword="true"/>；授权器拒绝，或连接已中止而取消时为 <see langword="false"/>。
    /// <see langword="false"/> 只表示这次没有订阅成功，不区分具体原因。
    /// </returns>
    public async Task<bool> Subscribe(string resourceKey)
    {
        var connectionAborted = Context.ConnectionAborted;
        // 租户来自环境上下文，由 SignalR 基座的 Hub 过滤器在本次调用前建立。
        var groupName = RealTimeGroups.Resource(resourceKey);
        try
        {
            var userId = currentUser.Id?.ToString() ?? Context.UserIdentifier;
            var allowed = await subscriptionAuthorizer.AuthorizeAsync(
                new RealTimeSubscriptionContext(resourceKey, userId),
                connectionAborted);

            if (!allowed)
            {
                logger.LogWarning("RealTimeHub Subscribe denied: connection={ConnectionId}, group={Group}", Context.ConnectionId, groupName);
                return false;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, groupName, connectionAborted);
        }
        catch (OperationCanceledException) when (connectionAborted.IsCancellationRequested)
        {
            logger.LogDebug("RealTimeHub Subscribe canceled: connection={ConnectionId} closed, group={Group}", Context.ConnectionId, groupName);
            return false;
        }

        logger.LogInformation("RealTimeHub Subscribe: connection={ConnectionId}, group={Group}", Context.ConnectionId, groupName);
        return true;
    }

    /// <summary>取消订阅资源变更。</summary>
    public async Task Unsubscribe(string resourceKey)
    {
        var connectionAborted = Context.ConnectionAborted;
        var groupName = RealTimeGroups.Resource(resourceKey);
        try
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName, connectionAborted);
        }
        catch (OperationCanceledException) when (connectionAborted.IsCancellationRequested)
        {
            logger.LogDebug("RealTimeHub Unsubscribe canceled: connection={ConnectionId} closed, group={Group}", Context.ConnectionId, groupName);
        }
    }
}
