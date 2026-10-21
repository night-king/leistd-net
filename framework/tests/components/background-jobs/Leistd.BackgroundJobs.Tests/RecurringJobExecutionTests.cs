using Leistd.BackgroundJobs.InProcess;
using Leistd.BackgroundJobs.InProcess.Recurring;
using Leistd.BackgroundJobs.Recurring;
using Leistd.BackgroundJobs.Tests.TestDoubles;
using Leistd.Lock.Abstractions;
using Leistd.Lock.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Leistd.BackgroundJobs.Tests;

/// <summary>单次执行：集群任务每个时段只成功执行一次，失败不记水位，每副本任务每次都执行。</summary>
/// <remarks>
/// 只加锁只能挡住并发，挡不住先后：副本 A 做完释放锁后，时钟稍慢的副本 B 在同一时段照样能拿到锁。
/// 这里用两次先后执行模拟两个副本，断言第二次靠水位跳过。
/// </remarks>
public sealed class RecurringJobExecutionTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 20, 10, 7, 30, TimeSpan.Zero));
    private readonly ServiceProvider _provider;
    private readonly JobLog _log = new();

    public RecurringJobExecutionTests()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddMemoryLocalLock()
            .AddInProcessBackgroundJobs()
            .AddRecurringJob<CountingJob>("test.cluster", RecurringJobSchedule.Every(TimeSpan.FromMinutes(5)), RecurringJobScope.Cluster)
            .AddRecurringJob<CountingJob>("test.instance", RecurringJobSchedule.Every(TimeSpan.FromMinutes(5)), RecurringJobScope.EveryInstance)
            .AddRecurringJob<FailingJob>("test.failing", RecurringJobSchedule.Every(TimeSpan.FromMinutes(5)), RecurringJobScope.Cluster);
        services.AddSingleton(_log);
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_time));
        _provider = services.BuildServiceProvider();
    }

    public void Dispose() => _provider.Dispose();

    private Task<RecurringJobRunOutcome> RunAsync(string name)
    {
        var definition = _provider.GetServices<RecurringJobDefinition>().Single(d => d.Name == name);
        return _provider.GetRequiredService<RecurringJobRunner>()
            .RunOnceAsync(definition, definition.ScheduleFactory(_provider), CancellationToken.None);
    }

    [Fact]
    public async Task A_cluster_job_runs_once_per_slot_and_again_in_the_next_slot()
    {
        Assert.Equal(RecurringJobRunOutcome.Completed, await RunAsync("test.cluster"));
        Assert.Equal(RecurringJobRunOutcome.SkippedCompleted, await RunAsync("test.cluster"));

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(RecurringJobRunOutcome.Completed, await RunAsync("test.cluster"));

        Assert.Equal(
            [new DateTimeOffset(2026, 9, 20, 10, 5, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 20, 10, 10, 0, TimeSpan.Zero)],
            _log.Runs.Select(r => r.Slot));
    }

    [Fact]
    public async Task A_cluster_job_is_skipped_while_another_runner_holds_its_lock()
    {
        var distributedLock = _provider.GetRequiredService<IDistributedLock>();
        await using (await distributedLock.LockAsync(RecurringJobRunner.LockKey("test.cluster")))
        {
            Assert.Equal(RecurringJobRunOutcome.SkippedLocked, await RunAsync("test.cluster"));
        }

        Assert.Empty(_log.Runs);
    }

    /// <summary>失败不记水位：下一次触发会重做，而不是被当成已完成跳过。</summary>
    [Fact]
    public async Task A_failed_run_leaves_the_slot_open()
    {
        Assert.Equal(RecurringJobRunOutcome.Failed, await RunAsync("test.failing"));
        Assert.Null(await _provider.GetRequiredService<IRecurringJobStateStore>().GetLastCompletedSlotAsync("test.failing"));
    }

    [Fact]
    public async Task An_every_instance_job_runs_each_time_without_a_watermark()
    {
        Assert.Equal(RecurringJobRunOutcome.Completed, await RunAsync("test.instance"));
        Assert.Equal(RecurringJobRunOutcome.Completed, await RunAsync("test.instance"));

        Assert.Equal(2, _log.Runs.Count);
    }

    /// <summary>集群任务没有分布式锁时启动即失败，而不是静默退化成每副本各跑一遍。</summary>
    [Fact]
    public async Task Cluster_jobs_without_a_distributed_lock_fail_at_startup()
    {
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddInProcessBackgroundJobs()
            .AddRecurringJob<FailingJob>("test.failing", RecurringJobSchedule.Every(TimeSpan.FromMinutes(5)), RecurringJobScope.Cluster)
            .BuildServiceProvider();
        var scheduler = provider.GetServices<IHostedService>().OfType<RecurringJobScheduler>().Single();

        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.StartAsync(CancellationToken.None));
    }
}
