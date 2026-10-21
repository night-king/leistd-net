using Leistd.EventBus.Events;

namespace Leistd.EventBus.Abstractions;

/// <summary>绕过 <see cref="ILocalEventDeferrer"/>、直接分发本地事件。</summary>
/// <remarks>
/// 供实现完成边界的组件排空待发队列，业务代码用 <see cref="ILocalEventBus"/>。
/// 本接口分发的事件不会被再次推迟；处理器内部新发布的事件走 <see cref="ILocalEventBus"/>，仍会被推迟并由排空循环带上。
/// </remarks>
public interface ILocalEventDispatcher
{
    /// <summary>立即按事件运行时类型分发给全部处理器。</summary>
    Task DispatchAsync(IEvent @event, CancellationToken cancellationToken = default);
}
