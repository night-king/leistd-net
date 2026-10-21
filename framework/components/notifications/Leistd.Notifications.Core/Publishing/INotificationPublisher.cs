using Leistd.Notifications.Dtos;

namespace Leistd.Notifications.Publishing;

/// <summary>通知发布器：业务发布通知的唯一入口，不感知底层传输。</summary>
/// <remarks>
/// 通知归属用户并持久化历史与已读状态；多人通知逐个发布。不需要历史的资源事件推送使用 realtime 组件。
/// </remarks>
public interface INotificationPublisher
{
    /// <summary>推送通知给指定用户，并写入该用户的通知历史。</summary>
    /// <remarks>记录的 ID、创建时刻与未读状态由发布器定案；同一份内容发给多个用户得到多条独立记录。</remarks>
    Task PublishToUserAsync(string userId, NotificationInputDto notification, CancellationToken ct = default);

}
