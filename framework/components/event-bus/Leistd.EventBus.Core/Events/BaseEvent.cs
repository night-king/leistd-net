namespace Leistd.EventBus.Events;

/// <summary>提供事件标识和发生时间的基类。</summary>
/// <remarks>默认取 <see cref="TimeProvider.System"/> 的当前时刻；需要控制事件时间时用带 <c>occurredOn</c> 的构造函数。</remarks>
public abstract class BaseEvent : IEvent
{
    /// <inheritdoc />
    public Guid EventId { get; }

    /// <inheritdoc />
    public DateTime OccurredOn { get; }

    /// <summary>使用系统时间源创建事件。</summary>
    protected BaseEvent() : this(TimeProvider.System.GetUtcNow().UtcDateTime)
    {
    }

    /// <summary>使用指定发生时间创建事件。</summary>
    /// <param name="occurredOn">事件发生时刻，应为 UTC。</param>
    protected BaseEvent(DateTime occurredOn)
    {
        // 有序 Guid：事件按 Id 排序即接近按发生顺序排序，与实体主键口径一致
        EventId = Guid.CreateVersion7();
        OccurredOn = occurredOn;
    }
}
