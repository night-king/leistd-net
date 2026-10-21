using Leistd.OperationRecords.Logging.Events;
using Leistd.OperationRecords.Logging.Recording;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Stores;
using Leistd.UnitOfWork;

namespace Leistd.OperationRecords.Logging.Stores;

// 有环境工作单元的成功记录在真实提交后写出；失败与无工作单元调用立即写出。
// 提交与输出之间仍有丢失窗口，输出失败不改变已提交业务的结果。
internal sealed class LoggingOperationRecordWriter(
    IUnitOfWorkManager unitOfWorkManager,
    OperationRecordLogEmitter emitter) : IOperationRecordWriter
{
    public Task InsertAsync(OperationRecordInfo record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Outcome == OperationRecordOutcome.Failed)
        {
            emitter.Emit(record);
            return Task.CompletedTask;
        }

        if (unitOfWorkManager.Current is { } unitOfWork)
        {
            unitOfWork.AddPendingEvents([new OperationRecordCommitted(record)]);
            return Task.CompletedTask;
        }

        emitter.EmitCommitted(record);
        return Task.CompletedTask;
    }
}
