namespace Leistd.BackgroundJobs.Recurring;

/// <summary>一个周期任务在本进程的状态快照。</summary>
/// <param name="Name">任务名。</param>
/// <param name="Scope">多副本下的执行范围。</param>
/// <param name="State">本进程的实际调度状态。</param>
/// <param name="Schedule">实际生效的排期；没有排上期（未启动、停用、排期无效）时为 <see langword="null"/>。</param>
/// <param name="CurrentSlot">读取时刻所在的时段起点；没有排期时为 <see langword="null"/>。只是当前时段，不代表此刻应已完成。</param>
/// <param name="LastCompletedSlot">
/// 共享水位记录的最近完成时段，只对 <see cref="RecurringJobScope.Cluster"/> 任务有意义；
/// 集群任务为 <see langword="null"/> 表示水位里没有记录，每副本任务不记水位、总为 <see langword="null"/>。
/// </param>
public sealed record RecurringJobStatus(
    string Name,
    RecurringJobScope Scope,
    RecurringJobScheduleState State,
    RecurringJobSchedule? Schedule,
    DateTimeOffset? CurrentSlot,
    DateTimeOffset? LastCompletedSlot);
