namespace Leistd.BackgroundJobs.Recurring;

/// <summary>记录 <see cref="RecurringJobScope.Cluster"/> 任务最近成功完成的时段。</summary>
/// <remarks>
/// 分布式锁只防并发，水位防止同一时段被时钟稍慢的副本先后重复执行。
/// 多副本部署必须使用共享存储的实现（如 EF 实现）；进程内默认实现只对单副本成立。
/// </remarks>
public interface IRecurringJobStateStore
{
    /// <summary>读取任务最近成功完成的时段；从未完成时为 <see langword="null"/>。</summary>
    /// <param name="jobName">任务名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<DateTimeOffset?> GetLastCompletedSlotAsync(string jobName, CancellationToken cancellationToken = default);

    /// <summary>记录任务成功完成了某个时段。</summary>
    /// <param name="jobName">任务名。</param>
    /// <param name="slot">时段起点。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SetLastCompletedSlotAsync(string jobName, DateTimeOffset slot, CancellationToken cancellationToken = default);
}
