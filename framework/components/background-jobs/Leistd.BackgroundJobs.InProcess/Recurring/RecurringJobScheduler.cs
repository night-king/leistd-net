using Leistd.BackgroundJobs.Options;
using Leistd.BackgroundJobs.Recurring;
using Leistd.Lock.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Leistd.BackgroundJobs.InProcess.Recurring;

// 进程内调度：每个登记的任务一条循环，按排期等待到点后交给 RecurringJobRunner。
internal sealed class RecurringJobScheduler(
    IEnumerable<RecurringJobDefinition> definitions,
    RecurringJobRunner runner,
    RecurringJobPlan plan,
    IServiceProvider serviceProvider,
    IOptions<BackgroundJobOptions> options,
    TimeProvider timeProvider,
    ILogger<RecurringJobScheduler> logger) : BackgroundService
{
    private readonly List<RecurringJobDefinition> _definitions = [.. definitions];

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // 启动期就失败：集群任务没有分布式锁可用时静默退化成每副本执行，会把维护任务在每个副本上各跑一遍
        if (options.Value.Enabled
            && _definitions.Any(d => d.Scope == RecurringJobScope.Cluster)
            && serviceProvider.GetService<IDistributedLock>() is null)
        {
            throw new InvalidOperationException(
                "Cluster-scoped recurring jobs are registered but no IDistributedLock is available. " +
                "Register a lock implementation (AddRedisDistributedLock for multiple replicas, AddMemoryLocalLock for one).");
        }

        return base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 总开关、停用名单与排期只在这里定一次，并登记给状态读取：监控看到的就是实际执行的这一套
        var planned = new Dictionary<string, PlannedJob>(StringComparer.Ordinal);
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Recurring jobs are disabled in this process.");
            foreach (var definition in _definitions)
            {
                planned[definition.Name] = new PlannedJob(RecurringJobScheduleState.DisabledInProcess, null);
            }

            plan.Publish(planned);
            return Task.CompletedTask;
        }

        var disabled = new HashSet<string>(options.Value.DisabledJobs, StringComparer.Ordinal);
        var loops = new List<Task>();
        foreach (var definition in _definitions)
        {
            if (disabled.Contains(definition.Name))
            {
                planned[definition.Name] = new PlannedJob(RecurringJobScheduleState.DisabledByName, null);
                continue;
            }

            RecurringJobSchedule schedule;
            try
            {
                schedule = definition.ScheduleFactory(serviceProvider);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Recurring job {Job} has an invalid schedule and will not run.", definition.Name);
                planned[definition.Name] = new PlannedJob(RecurringJobScheduleState.InvalidSchedule, null);
                continue;
            }

            planned[definition.Name] = new PlannedJob(RecurringJobScheduleState.Active, schedule);
            loops.Add(RunLoopAsync(definition, schedule, stoppingToken));
        }

        plan.Publish(planned);
        return Task.WhenAll(loops);
    }

    private async Task RunLoopAsync(RecurringJobDefinition definition, RecurringJobSchedule schedule, CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Recurring job {Job} scheduled {Schedule} ({Scope}).", definition.Name, schedule, definition.Scope);

        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            var due = first && schedule.MaxStartupJitter > TimeSpan.Zero
                ? now + TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * schedule.MaxStartupJitter.TotalMilliseconds)
                : schedule.GetNextRun(now);
            first = false;

            try
            {
                // 计时器会比到期时刻早几毫秒触发（毫秒取整、粗粒度单调时钟），没到点就补等余下的时间。
                // 提前进入执行会按上一时段判为已完成而跳过，读水位期间越过边界后再按当前时刻排期，就整段漏掉本时段。
                // 余量向上取整到毫秒：不足 1 毫秒的等待会立即完成，变成空转
                for (var delay = due - now; delay > TimeSpan.Zero; delay = due - timeProvider.GetUtcNow())
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(delay.TotalMilliseconds)), timeProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await runner.RunOnceAsync(definition, schedule, stoppingToken);
        }
    }
}
