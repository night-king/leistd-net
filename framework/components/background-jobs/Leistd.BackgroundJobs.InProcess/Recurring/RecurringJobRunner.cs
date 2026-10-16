using System.Collections.Concurrent;
using Leistd.BackgroundJobs.Recurring;
using Leistd.Lock.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Leistd.BackgroundJobs.InProcess.Recurring;

// 一次执行：在本轮的作用域里先判任务自身的开关，再按范围决定是否抢锁、比对水位，最后在同一作用域里执行任务。
// 开关与任务共用一个作用域：读 IOptionsSnapshot 的两边看到同一份配置，不会按“开启”放行、又拿到热更新后的“关闭”参数。
// 失败只记日志、不写水位，本轮不重试：下一次触发（最迟是下一个调度时段）会重做，因此任务必须幂等并扫描历史积压。
internal sealed class RecurringJobRunner(
    IServiceScopeFactory scopeFactory,
    IServiceProvider rootProvider,
    TimeProvider timeProvider,
    ILogger<RecurringJobRunner> logger)
{
    // 每个任务上一次观测到的开关，只在切换时记日志
    private readonly ConcurrentDictionary<string, bool> _lastSwitch = new(StringComparer.Ordinal);

    internal static string LockKey(string jobName) => $"background-jobs:{jobName}";

    public async Task<RecurringJobRunOutcome> RunOnceAsync(
        RecurringJobDefinition definition,
        RecurringJobSchedule schedule,
        CancellationToken stoppingToken)
    {
        var slot = schedule.GetSlot(timeProvider.GetUtcNow());

        try
        {
            await using var jobScope = scopeFactory.CreateAsyncScope();
            if (!IsSwitchedOn(definition, jobScope.ServiceProvider))
            {
                return RecurringJobRunOutcome.SkippedDisabled;
            }

            if (definition.Scope == RecurringJobScope.EveryInstance)
            {
                await ExecuteAsync(definition, jobScope.ServiceProvider, slot, stoppingToken);
                return RecurringJobRunOutcome.Completed;
            }

            var distributedLock = rootProvider.GetRequiredService<IDistributedLock>();

            // 零等待：别的副本正在跑这个时段，这一份直接让出，而不是排队等它跑完再重复一遍
            await using var handle = await distributedLock.TryLockAsync(LockKey(definition.Name), TimeSpan.Zero, stoppingToken);
            if (handle is null)
            {
                logger.LogDebug("Recurring job {Job} is running elsewhere; skipping slot {Slot:o}.", definition.Name, slot);
                return RecurringJobRunOutcome.SkippedLocked;
            }

            using var run = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, handle.LockLost);
            await using var stateScope = scopeFactory.CreateAsyncScope();
            var stateStore = stateScope.ServiceProvider.GetRequiredService<IRecurringJobStateStore>();

            if (await stateStore.GetLastCompletedSlotAsync(definition.Name, run.Token) is { } completed && completed >= slot)
            {
                logger.LogDebug("Recurring job {Job} already completed slot {Slot:o}; skipping.", definition.Name, slot);
                return RecurringJobRunOutcome.SkippedCompleted;
            }

            await ExecuteAsync(definition, jobScope.ServiceProvider, slot, run.Token);
            await stateStore.SetLastCompletedSlotAsync(definition.Name, slot, run.Token);
            return RecurringJobRunOutcome.Completed;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return RecurringJobRunOutcome.Canceled;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Recurring job {Job} lost its cluster lock during slot {Slot:o}; it will retry.", definition.Name, slot);
            return RecurringJobRunOutcome.Failed;
        }
        catch (Exception exception)
        {
            // 开关求值抛出也走这里：按失败处理，不当成关闭。不让异常冒出调度循环：BackgroundService 的未处理异常默认会停掉整个宿主
            logger.LogError(exception, "Recurring job {Job} failed for slot {Slot:o}; it will retry.", definition.Name, slot);
            return RecurringJobRunOutcome.Failed;
        }
    }

    private bool IsSwitchedOn(RecurringJobDefinition definition, IServiceProvider scope)
    {
        var on = definition.IsEnabled?.Invoke(scope) ?? true;
        // 首次观测到开启不记；关闭与之后的每次切换各记一条
        var previous = _lastSwitch.TryGetValue(definition.Name, out var last) ? last : true;
        _lastSwitch[definition.Name] = on;
        if (previous != on)
        {
            if (on)
            {
                logger.LogInformation("Recurring job {Job} is switched on again; it resumes at its scheduled runs.", definition.Name);
            }
            else
            {
                logger.LogInformation("Recurring job {Job} is switched off; its scheduled runs are skipped and not recorded as completed.", definition.Name);
            }
        }

        return on;
    }

    private static async Task ExecuteAsync(
        RecurringJobDefinition definition, IServiceProvider scope, DateTimeOffset slot, CancellationToken cancellationToken)
    {
        var job = (IRecurringJob)scope.GetRequiredService(definition.JobType);
        await job.ExecuteAsync(new RecurringJobContext(definition.Name, slot), cancellationToken);
    }
}

internal enum RecurringJobRunOutcome
{
    Completed,
    SkippedDisabled,
    SkippedLocked,
    SkippedCompleted,
    Failed,
    Canceled
}
