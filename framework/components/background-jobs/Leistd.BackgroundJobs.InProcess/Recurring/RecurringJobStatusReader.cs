using Leistd.BackgroundJobs.Recurring;
using Microsoft.Extensions.DependencyInjection;

namespace Leistd.BackgroundJobs.InProcess.Recurring;

// 状态读取：调度事实取调度器登记的计划，任务自身的开关在新作用域里求值，集群任务的水位取共享存储（与是否排期无关）
internal sealed class RecurringJobStatusReader(
    IEnumerable<RecurringJobDefinition> definitions,
    RecurringJobPlan plan,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : IRecurringJobStatusReader
{
    private readonly List<RecurringJobDefinition> _definitions = [.. definitions];

    public async Task<IReadOnlyList<RecurringJobStatus>> GetStatusesAsync(CancellationToken cancellationToken = default)
    {
        // 整批共用一个读取时刻，各任务的当前时段可互相比较
        var now = timeProvider.GetUtcNow();
        var jobs = plan.Jobs;
        var statuses = new List<RecurringJobStatus>(_definitions.Count);
        foreach (var definition in _definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var scope = scopeFactory.CreateAsyncScope();

            // 本进程是否排期与共享水位里有没有完成记录是两件事：没排上期的集群任务照样读水位，
            // 读取失败就让整次读取失败，不把失败显示成“没有记录”
            DateTimeOffset? lastCompleted = definition.Scope == RecurringJobScope.Cluster
                ? await scope.ServiceProvider.GetRequiredService<IRecurringJobStateStore>()
                    .GetLastCompletedSlotAsync(definition.Name, cancellationToken)
                : null;

            if (jobs is null || !jobs.TryGetValue(definition.Name, out var planned))
            {
                statuses.Add(new RecurringJobStatus(
                    definition.Name, definition.Scope, RecurringJobScheduleState.NotStarted, null, null, lastCompleted));
                continue;
            }

            if (planned.Schedule is not { } schedule)
            {
                statuses.Add(new RecurringJobStatus(definition.Name, definition.Scope, planned.State, null, null, lastCompleted));
                continue;
            }

            // 求值抛出就让整次读取失败：不能把“判断不了”显示成“已关闭”
            var state = definition.IsEnabled?.Invoke(scope.ServiceProvider) is false
                ? RecurringJobScheduleState.SwitchedOff
                : RecurringJobScheduleState.Active;
            statuses.Add(new RecurringJobStatus(
                definition.Name, definition.Scope, state, schedule, schedule.GetSlot(now), lastCompleted));
        }

        return statuses;
    }
}
