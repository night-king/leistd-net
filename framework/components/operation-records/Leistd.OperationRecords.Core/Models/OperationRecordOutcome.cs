namespace Leistd.OperationRecords.Models;

/// <summary>一次操作的结果。</summary>
/// <remarks>只有两档；“部分成功”“重试中”这类中间态由业务自己的实体表达。</remarks>
public enum OperationRecordOutcome
{
    /// <summary>操作完成。</summary>
    Succeeded,

    /// <summary>操作被拒绝或失败：权限不足（授权阶段）、业务规则拒绝，或执行中抛错。</summary>
    Failed
}
