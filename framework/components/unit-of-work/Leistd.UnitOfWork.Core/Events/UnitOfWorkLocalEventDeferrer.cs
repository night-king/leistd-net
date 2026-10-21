using Leistd.EventBus.Abstractions;
using Leistd.EventBus.Events;

namespace Leistd.UnitOfWork.Events;

// 将活动工作单元内的本地事件交给提交阶段调度，避免过早执行。
// AfterCommit 没有环境工作单元，因此其内部新发布的事件仍立即分发。
internal sealed class UnitOfWorkLocalEventDeferrer(IUnitOfWorkManager unitOfWorkManager) : ILocalEventDeferrer
{
    /// <inheritdoc />
    public bool TryDefer(IEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (@event is not ILocalEvent localEvent)
        {
            return false;
        }

        var unitOfWork = unitOfWorkManager.Current;
        if (unitOfWork is null)
        {
            return false;
        }

        unitOfWork.AddPendingEvents([localEvent]);
        return true;
    }
}
