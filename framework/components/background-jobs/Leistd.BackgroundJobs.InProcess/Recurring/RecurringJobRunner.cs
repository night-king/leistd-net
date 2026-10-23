using System.Collections.Concurrent;
using System.Diagnostics;
using Leistd.BackgroundJobs.InProcess.Diagnostics;
using Leistd.BackgroundJobs.Recurring;
using Leistd.Lock.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Leistd.BackgroundJobs.InProcess.Recurring;

// 一次执行：在本轮的作用域里先判任务自身的开关，再按范围决定是否抢锁、比对水位，最后在同一作用域里执行任务。
// 开关与任务共用一个作用域：读 IOptionsSnapshot 的两边看到同一份配置，不会按“开启”放行、又拿到热更新后的“关闭”参数。
// 失败报告给观察者并记日志、不写水位，本轮不重试：下一次触发（最迟是下一个调度时段）会重做，因此任务必须幂等并扫描历史积压。
internal sealed class RecurringJobRunner(
    IServiceScopeFactory scopeFactory,
    IServiceProvider rootProvider,
    TimeProvider timeProvider,
    ILogger<RecurringJobRunner> logger)
{
    // 每个任务上一次观测到的开关，只在切换时记日志
    private readonly ConcurrentDictionary<string, bool> _lastSwitch = new(StringComparer.Ordinal);

    internal static string LockKey(string jobName) => $"background-jobs:{jobName}";

    public async Task<RecurringJobRunnerResult> RunOnceAsync(
        RecurringJobDefinition definition,
        RecurringJobSchedule schedule,
        CancellationToken stoppingToken)
    {
        var slot = schedule.GetSlot(timeProvider.GetUtcNow());

        var previous = Activity.Current;
        Activity.Current = null;
        using var activity = BackgroundJobDiagnostics.StartActivity("Leistd.RecurringJob");
        activity.SetTag("leistd.job.name", definition.Name);
        activity.SetTag("leistd.job.slot", slot.ToString("O"));
        var lockLost = CancellationToken.None;

        try
        {
            await using var jobScope = scopeFactory.CreateAsyncScope();
            if (!IsSwitchedOn(definition, jobScope.ServiceProvider))
            {
                return RecurringJobRunnerResult.SkippedDisabled;
            }

            if (definition.Scope == RecurringJobScope.EveryInstance)
            {
                return await RunObservedAsync(definition, jobScope.ServiceProvider, slot, stoppingToken, lockLost, stoppingToken, null, activity);
            }

            var distributedLock = rootProvider.GetRequiredService<IDistributedLock>();

            // 零等待：别的副本正在跑这个时段，这一份直接让出，而不是排队等它跑完再重复一遍
            await using var handle = await distributedLock.TryLockAsync(LockKey(definition.Name), TimeSpan.Zero, stoppingToken);
            if (handle is null)
            {
                logger.LogDebug("Recurring job {Job} is running elsewhere; skipping slot {Slot:o}.", definition.Name, slot);
                return RecurringJobRunnerResult.SkippedLocked;
            }

            lockLost = handle.LockLost;
            using var run = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lockLost);
            await using var stateScope = scopeFactory.CreateAsyncScope();
            var stateStore = stateScope.ServiceProvider.GetRequiredService<IRecurringJobStateStore>();

            if (await stateStore.GetLastCompletedSlotAsync(definition.Name, run.Token) is { } completed && completed >= slot)
            {
                logger.LogDebug("Recurring job {Job} already completed slot {Slot:o}; skipping.", definition.Name, slot);
                return RecurringJobRunnerResult.SkippedCompleted;
            }

            return await RunObservedAsync(definition, jobScope.ServiceProvider, slot, stoppingToken, lockLost, run.Token, stateStore, activity);
        }
        catch (Exception exception)
        {
            // 开始之前的故障仍须隔离，不能冒出 BackgroundService 停掉宿主。
            var outcome = ClassifyFailure(exception, stoppingToken, lockLost);
            LogOutcome(definition.Name, slot, outcome, exception, activity);
            return ToRunnerResult(outcome);
        }
        finally
        {
            activity.Stop();
            Activity.Current = previous;
        }
    }

    private async Task<RecurringJobRunnerResult> RunObservedAsync(
        RecurringJobDefinition definition, IServiceProvider scope, DateTimeOffset slot,
        CancellationToken stoppingToken, CancellationToken lockLost, CancellationToken runToken,
        IRecurringJobStateStore? stateStore, Activity activity)
    {
        var observers = ResolveObservers(scope, definition.Name);
        var run = new RecurringJobRun(definition.Name, definition.Scope, slot, timeProvider.GetUtcNow());
        var timestamp = timeProvider.GetTimestamp();
        var started = 0;
        var outcome = RecurringJobRunOutcome.Succeeded;
        Exception? failure = null;
        try
        {
            foreach (var observer in observers)
            {
                runToken.ThrowIfCancellationRequested();
                // 抛出的 Started 也必须配对 Completed。
                started++;
                await NotifyStartedAsync(observer, run, runToken);
            }

            runToken.ThrowIfCancellationRequested();
            await ExecuteAsync(definition, scope, slot, runToken);
            // 任务可能吞掉取消；水位存储也不一定主动检查令牌。
            runToken.ThrowIfCancellationRequested();
            if (stateStore is not null)
            {
                await stateStore.SetLastCompletedSlotAsync(definition.Name, slot, runToken);
            }
        }
        catch (Exception exception)
        {
            outcome = ClassifyFailure(exception, stoppingToken, lockLost);
            failure = outcome == RecurringJobRunOutcome.Failed ? exception : exception as OperationCanceledException;
            LogOutcome(definition.Name, slot, outcome, exception, activity);
        }
        finally
        {
            activity.SetTag("leistd.job.outcome", outcome.ToString());
            var result = new RecurringJobRunResult(outcome, timeProvider.GetUtcNow(), timeProvider.GetElapsedTime(timestamp), failure);
            for (var index = 0; index < started; index++)
            {
                try
                {
                    await observers[index].OnRunCompletedAsync(run, result);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Recurring job {Job} observer {Observer} failed during Completed.", run.JobName, observers[index].GetType());
                }
            }
        }

        return ToRunnerResult(outcome);
    }

    private IRecurringJobRunObserver[] ResolveObservers(IServiceProvider scope, string jobName)
    {
        try
        {
            return scope.GetServices<IRecurringJobRunObserver>().ToArray();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Recurring job {Job} observers could not be resolved; this run will not be observed.", jobName);
            return [];
        }
    }

    private async Task NotifyStartedAsync(IRecurringJobRunObserver observer, RecurringJobRun run, CancellationToken runToken)
    {
        try
        {
            await observer.OnRunStartedAsync(run, runToken);
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {
            // 外层统一确定停机或失锁；本次令牌取消不是观察者故障。
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Recurring job {Job} observer {Observer} failed during Started.", run.JobName, observer.GetType());
        }
    }

    private static RecurringJobRunOutcome ClassifyFailure(
        Exception exception, CancellationToken stoppingToken, CancellationToken lockLost)
    {
        if (exception is not OperationCanceledException)
        {
            return RecurringJobRunOutcome.Failed;
        }

        return stoppingToken.IsCancellationRequested ? RecurringJobRunOutcome.Canceled
            : lockLost.IsCancellationRequested ? RecurringJobRunOutcome.LockLost
            : RecurringJobRunOutcome.Failed;
    }

    private void LogOutcome(string jobName, DateTimeOffset slot, RecurringJobRunOutcome outcome, Exception exception, Activity activity)
    {
        activity.SetTag("leistd.job.outcome", outcome.ToString());
        if (outcome == RecurringJobRunOutcome.LockLost)
        {
            activity.SetStatus(ActivityStatusCode.Error);
            logger.LogWarning("Recurring job {Job} lost its cluster lock during slot {Slot:o}; it will retry.", jobName, slot);
        }
        else if (outcome == RecurringJobRunOutcome.Failed)
        {
            activity.SetStatus(ActivityStatusCode.Error);
            logger.LogError(exception, "Recurring job {Job} failed for slot {Slot:o}; it will retry.", jobName, slot);
        }
    }

    private static RecurringJobRunnerResult ToRunnerResult(RecurringJobRunOutcome outcome)
        => outcome == RecurringJobRunOutcome.Succeeded ? RecurringJobRunnerResult.Completed
            : outcome == RecurringJobRunOutcome.Canceled ? RecurringJobRunnerResult.Canceled
            : RecurringJobRunnerResult.Failed;

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

internal enum RecurringJobRunnerResult
{
    Completed,
    SkippedDisabled,
    SkippedLocked,
    SkippedCompleted,
    Failed,
    Canceled
}
