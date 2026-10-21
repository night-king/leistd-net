namespace Leistd.BackgroundJobs.EntityFrameworkCore.Entities;

/// <summary>集群周期任务的完成水位：每个任务一行。</summary>
public class RecurringJobState
{
    /// <summary>任务名长度上限。</summary>
    public const int MaxNameLength = 200;

    /// <summary>任务名（主键）。</summary>
    public string Name { get; set; } = default!;

    /// <summary>最近成功完成的时段起点（UTC）。</summary>
    public DateTime LastCompletedSlot { get; set; }
}
