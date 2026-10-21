using Leistd.Notifications.Channels;
using Leistd.Notifications.Dtos;
using Microsoft.AspNetCore.SignalR;

namespace Leistd.Notifications.AspNetCore.SignalR.Channels;

// 基于 SignalR 的通知外发渠道，经注册时选定的 Hub 推送（见 NotificationHubClients）。
// 不记日志、不吞异常：失败隔离与记录由发布器统一负责（见 INotificationChannel）。
internal sealed class SignalRNotificationChannel(NotificationHubClients hub) : INotificationChannel
{
    /// <inheritdoc />
    /// <remarks>实时推送是站内通知的一部分，与通知历史同进同退。</remarks>
    public string Name => INotificationChannel.InAppName;

    /// <inheritdoc />
    public async Task DeliverAsync(string userId, NotificationOutputDto notification, CancellationToken ct = default)
    {
        // 按 UserIdentifier 寻址，其生成规则由 SignalR 基座的 UserIdProvider 提供
        await hub.Clients.User(userId).SendAsync(NotificationClientMethods.Received, notification, ct);
    }
}
