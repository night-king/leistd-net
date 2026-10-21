namespace Leistd.RealTime.Publishing;

/// <summary>业务事件推送器：把事件推给订阅了某资源的客户端，不持久化。</summary>
/// <remarks>需要历史与已读状态的用户通知使用 notifications 组件。</remarks>
public interface IBusinessEventPublisher
{
    /// <summary>推送事件给订阅了指定资源的客户端。</summary>
    /// <typeparam name="TEvent">事件数据类型。</typeparam>
    /// <param name="resourceKey">资源标识，如 <c>product-profile:{ownerId}</c>。</param>
    /// <param name="eventName">客户端监听的事件名，如 <c>ProductProfileUpdated</c>。</param>
    /// <param name="event">事件数据。</param>
    /// <param name="ct">取消令牌。</param>
    Task PublishToResourceAsync<TEvent>(string resourceKey, string eventName, TEvent @event, CancellationToken ct = default)
        where TEvent : class;
}
