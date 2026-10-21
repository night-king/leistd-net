using Leistd.EventBus.Abstractions;
using Leistd.EventBus.Events;

namespace Leistd.TestBase.Doubles;

/// <summary>只记录、不分发的本地事件总线，用于断言被测代码发布了哪些事件。</summary>
public sealed class RecordingEventBus : ILocalEventBus
{
    /// <summary>按发布顺序记录的事件。</summary>
    public List<IEvent> Published { get; } = [];

    /// <inheritdoc/>
    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IEvent
    {
        Published.Add(@event);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task PublishAsync(IEvent @event, CancellationToken cancellationToken = default)
    {
        Published.Add(@event);
        return Task.CompletedTask;
    }
}
