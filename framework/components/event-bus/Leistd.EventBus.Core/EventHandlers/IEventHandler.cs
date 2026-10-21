using Leistd.EventBus.Events;

namespace Leistd.EventBus.EventHandlers;

/// <summary>处理指定类型的事件。</summary>
public interface IEventHandler<in TEvent> where TEvent : IEvent
{
    /// <summary>处理一个事件；抛出时其余处理器照常执行，发布方最终收到异常。</summary>
    Task HandleAsync(TEvent @event, CancellationToken cancellationToken = default);
}
