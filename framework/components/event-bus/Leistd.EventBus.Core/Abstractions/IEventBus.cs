using System.Runtime.CompilerServices;
using Leistd.EventBus.Events;

namespace Leistd.EventBus.Abstractions;

/// <summary>按事件运行时类型路由并发布事件。</summary>
public interface IEventBus
{
    /// <summary>发布事件；按运行时类型路由，与非泛型重载行为一致。</summary>
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IEvent;

    /// <summary>按运行时类型路由发布事件。</summary>
    /// <remarks>重载优先级更高：以 <c>IEvent</c> / <c>ILocalEvent</c> 静态类型发布时选中此重载。</remarks>
    [OverloadResolutionPriority(1)]
    Task PublishAsync(IEvent @event, CancellationToken cancellationToken = default);
}
