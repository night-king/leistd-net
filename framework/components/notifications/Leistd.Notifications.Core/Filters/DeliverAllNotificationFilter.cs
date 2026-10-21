using Leistd.Notifications.Dtos;

namespace Leistd.Notifications.Filters;

// 默认过滤器：一律投递。宿主没有通知偏好时就是这个行为。
internal sealed class DeliverAllNotificationFilter : INotificationDeliveryFilter
{
    public Task<bool> ShouldDeliverAsync(string userId, NotificationOutputDto notification, string channel, CancellationToken ct = default)
        => Task.FromResult(true);
}
