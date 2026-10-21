using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Leistd.RealTime.AspNetCore.SignalR.Hubs;
using Leistd.RealTime.Publishing;

namespace Leistd.RealTime.AspNetCore.SignalR.Publishing;

// IBusinessEventPublisher 的 SignalR 实现：推送到 resource:{key} 组。
internal class SignalRBusinessEventPublisher(
    IHubContext<RealTimeHub> realTimeHub,
    ILogger<SignalRBusinessEventPublisher> logger) : IBusinessEventPublisher
{
    public async Task PublishToResourceAsync<TEvent>(
        string resourceKey,
        string eventName,
        TEvent @event,
        CancellationToken ct = default)
        where TEvent : class
    {
        // 与订阅侧共用组名生成处
        var groupName = RealTimeGroups.Resource(resourceKey);
        try
        {
            await realTimeHub.Clients.Group(groupName).SendAsync(eventName, @event, ct);
            logger.LogInformation("Event {EventName} pushed to group {Group}", eventName, groupName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to push event {EventName} to group {Group}", eventName, groupName);
        }
    }
}
