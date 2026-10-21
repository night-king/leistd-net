namespace Leistd.EventBus.Events;

/// <summary>进程内发布的事件基类。</summary>
public abstract class LocalEvent : BaseEvent, ILocalEvent
{
    /// <summary>以系统当前时刻作为发生时间。</summary>
    protected LocalEvent()
    {
    }

    /// <summary>以指定时刻作为发生时间，使事件时间与发布方的时间源一致。</summary>
    /// <param name="occurredOn">事件发生时刻，应为 UTC。</param>
    protected LocalEvent(DateTime occurredOn) : base(occurredOn)
    {
    }
}
