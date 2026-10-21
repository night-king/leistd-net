namespace Leistd.EventBus.Events;

/// <summary>标记只在当前进程内发布的事件。</summary>
public interface ILocalEvent : IEvent
{
}
