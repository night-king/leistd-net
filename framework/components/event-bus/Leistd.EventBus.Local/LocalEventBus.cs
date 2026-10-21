using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Leistd.EventBus.Events;
using Leistd.EventBus.Local.Wrappers;
using Leistd.EventBus.Abstractions;

namespace Leistd.EventBus.Local;

/// <summary>在当前进程中按事件运行时类型分发事件。</summary>
public class LocalEventBus(
    IServiceScopeFactory serviceScopeFactory,
    ILocalEventDeferrer? deferrer = null) : ILocalEventBus, ILocalEventDispatcher
{
    private static readonly ConcurrentDictionary<Type, EventHandlerWrapper> _wrapperCache = new();

    /// <inheritdoc />
    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IEvent
        => PublishAsync((IEvent)@event!, cancellationToken);

    /// <inheritdoc />
    [OverloadResolutionPriority(1)]
    public Task PublishAsync(IEvent @event, CancellationToken cancellationToken = default)
    {
        // 活动工作单元内推迟发布，确保处理器只在声明的事务阶段执行。
        if (deferrer?.TryDefer(@event) == true)
        {
            return Task.CompletedTask;
        }

        return DispatchAsync(@event, cancellationToken);
    }

    /// <inheritdoc />
    public Task DispatchAsync(IEvent @event, CancellationToken cancellationToken = default)
    {
        var eventType = @event.GetType();

        var wrapper = _wrapperCache.GetOrAdd(eventType, t =>
        {
            var wrapperType = typeof(EventHandlerWrapperImpl<>).MakeGenericType(t);
            return (EventHandlerWrapper)Activator.CreateInstance(wrapperType)!;
        });

        return wrapper.HandleAsync(@event, serviceScopeFactory, cancellationToken);
    }
}
