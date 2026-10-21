using Leistd.EventBus.Events;

namespace Leistd.EventBus.Abstractions;

/// <summary>完成边界对本地事件发布的介入点：有活动边界时把事件推迟到该边界完成时发布。</summary>
/// <remarks>实现由拥有完成边界的组件（如工作单元）提供；没有注册实现时总线立即分发。</remarks>
public interface ILocalEventDeferrer
{
    /// <summary>尝试把事件交给当前活动的完成边界。</summary>
    /// <param name="event">待发布的事件。</param>
    /// <returns>
    /// <see langword="true"/> 表示已接管，调用方不再分发；
    /// <see langword="false"/> 表示当前没有活动边界或该事件不适用，调用方照常分发。
    /// </returns>
    bool TryDefer(IEvent @event);
}
