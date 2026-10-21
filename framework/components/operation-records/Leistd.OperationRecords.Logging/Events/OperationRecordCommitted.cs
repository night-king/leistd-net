using Leistd.EventBus.Events;
using Leistd.OperationRecords.Models;

namespace Leistd.OperationRecords.Logging.Events;

// 环境工作单元里的成功记录：随工作单元的待发事件排队，提交之后才写出。
// 携带的是记录器当场冻结的记录（标识、时间、操作人、租户都已定案），提交后不再读取任何上下文。
internal sealed class OperationRecordCommitted(OperationRecordInfo record) : LocalEvent
{
    public OperationRecordInfo Record { get; } = record;
}
