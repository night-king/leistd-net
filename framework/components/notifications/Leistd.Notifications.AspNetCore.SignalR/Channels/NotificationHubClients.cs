using Microsoft.AspNetCore.SignalR;

namespace Leistd.Notifications.AspNetCore.SignalR.Channels;

// 通知推送所经的 Hub。渠道只依赖这个非泛型基类，因此只有一个实现类型；
// 选定的 Hub 由注册的实现类型 NotificationHubClients<THub> 表达，冲突检查读的也是它。
internal abstract class NotificationHubClients
{
    public abstract IHubClients Clients { get; }
}

internal sealed class NotificationHubClients<THub>(IHubContext<THub> hub) : NotificationHubClients
    where THub : Hub
{
    public override IHubClients Clients => hub.Clients;
}
