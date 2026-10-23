namespace Leistd.BackgroundJobs.Recurring;

/// <summary>周期任务的一次执行结果。</summary>
public enum RecurringJobRunOutcome
{
    /// <summary>任务完成，集群任务的完成水位已成功写入。</summary>
    Succeeded = 1,
    /// <summary>任务解析、执行或水位写入失败。</summary>
    Failed = 2,
    /// <summary>宿主停机取消；与失锁同时发生时优先。</summary>
    Canceled = 3,
    /// <summary>执行期间失去集群锁。</summary>
    LockLost = 4
}
