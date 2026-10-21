using Leistd.Notifications.Channels;
using Leistd.Notifications.Dtos;

namespace Leistd.Notifications.Filters;

/// <summary>决定一条通知是否经某个渠道投给某个收件人（收件人的通知偏好在这里生效）。</summary>
/// <remarks>
/// <para>默认实现一律投递。宿主要按用户偏好过滤时注册自己的实现：在 <c>AddNotifications</c> 之前注册即可保留，
/// 之后注册则用 <c>Replace</c> 覆盖。</para>
/// <para>站内渠道（<see cref="INotificationChannel.InAppName"/>）同时决定是否写入通知历史：
/// 发布器先问它，不投则既不落库也不实时推送。其余渠道逐个问。</para>
/// <para>过滤失败（例如读偏好时出错）会让本次发布失败，不按“投递”或“不投递”猜测。</para>
/// </remarks>
public interface INotificationDeliveryFilter
{
    /// <summary>这条通知是否经 <paramref name="channel"/> 投给 <paramref name="userId"/>。</summary>
    /// <param name="userId">收件人。</param>
    /// <param name="notification">已定案的通知（按 <c>Type</c> 区分类别）。</param>
    /// <param name="channel">渠道名，见 <see cref="INotificationChannel.Name"/>。</param>
    /// <param name="ct">取消令牌。</param>
    Task<bool> ShouldDeliverAsync(string userId, NotificationOutputDto notification, string channel, CancellationToken ct = default);
}
