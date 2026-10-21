namespace Leistd.EventBus.Events;

/// <summary>可追踪的事件元数据。</summary>
public interface IEvent
{
    /// <summary>用于幂等和追踪的事件标识。</summary>
    Guid EventId { get; }

    /// <summary>事件的发生时间（UTC）。</summary>
    DateTime OccurredOn { get; }
}
