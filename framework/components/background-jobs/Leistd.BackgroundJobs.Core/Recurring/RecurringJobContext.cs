namespace Leistd.BackgroundJobs.Recurring;

/// <summary>一次周期执行的信息。</summary>
/// <param name="JobName">登记时的任务名。</param>
/// <param name="Slot">本次执行所属时段的起点（UTC）；同一时段内的重复触发共享这个值。</param>
public sealed record RecurringJobContext(string JobName, DateTimeOffset Slot);
