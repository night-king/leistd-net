namespace Leistd.BackgroundJobs.Recurring;

/// <summary>一次已开始的周期执行。</summary>
/// <param name="JobName">登记时的任务名。</param>
/// <param name="Scope">任务的执行范围。</param>
/// <param name="Slot">本次执行所属时段的起点（UTC）。</param>
/// <param name="StartedAt">开始回调前的 UTC 时刻，不包含抢锁和读取水位。</param>
public sealed record RecurringJobRun(
    string JobName, RecurringJobScope Scope, DateTimeOffset Slot, DateTimeOffset StartedAt);
