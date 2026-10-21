using System.Collections.Concurrent;
using Leistd.BackgroundJobs.Recurring;

namespace Leistd.BackgroundJobs.InProcess.Recurring;

// 进程内水位：只对单副本成立。多副本部署换成共享存储的实现（如 EF 实现），
// 否则时钟稍慢的副本会在同一时段再执行一遍。
internal sealed class InMemoryRecurringJobStateStore : IProcessLocalRecurringJobStateStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _completed = new(StringComparer.Ordinal);

    public Task<DateTimeOffset?> GetLastCompletedSlotAsync(string jobName, CancellationToken cancellationToken = default)
        => Task.FromResult(_completed.TryGetValue(jobName, out var slot) ? slot : (DateTimeOffset?)null);

    public Task SetLastCompletedSlotAsync(string jobName, DateTimeOffset slot, CancellationToken cancellationToken = default)
    {
        _completed.AddOrUpdate(jobName, slot, (_, current) => slot > current ? slot : current);
        return Task.CompletedTask;
    }
}
