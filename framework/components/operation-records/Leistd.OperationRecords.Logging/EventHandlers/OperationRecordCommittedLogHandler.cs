using Leistd.OperationRecords.Logging.Events;
using Leistd.OperationRecords.Logging.Recording;
using Leistd.EventBus.EventHandlers;
using Leistd.UnitOfWork.Events;

namespace Leistd.OperationRecords.Logging.EventHandlers;

// 只在 AfterCommit 阶段执行：回滚或提交失败时工作单元不会进入这个阶段，成功记录也就不会出现。
[UnitOfWorkEventHandler(UnitOfWorkPhase.AfterCommit)]
internal sealed class OperationRecordCommittedLogHandler(OperationRecordLogEmitter emitter)
    : IEventHandler<OperationRecordCommitted>
{
    public Task HandleAsync(OperationRecordCommitted @event, CancellationToken cancellationToken = default)
    {
        emitter.EmitCommitted(@event.Record);
        return Task.CompletedTask;
    }
}
