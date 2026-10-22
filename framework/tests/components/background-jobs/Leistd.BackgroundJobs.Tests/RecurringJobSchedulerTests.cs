using System.Collections.Concurrent;
using System.Threading.Channels;
using Leistd.BackgroundJobs.InProcess;
using Leistd.BackgroundJobs.InProcess.Recurring;
using Leistd.BackgroundJobs.Options;
using Leistd.BackgroundJobs.Recurring;
using Leistd.Lock.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Leistd.BackgroundJobs.Tests;

/// <summary>真实调度循环的启停、禁用与故障隔离；时间由官方假时钟推进。</summary>
public sealed class RecurringJobSchedulerTests
{
    private static readonly TimeSpan FailSafe = TimeSpan.FromSeconds(5);
    private static readonly RecurringJobSchedule Daily = RecurringJobSchedule.DailyAt(new TimeOnly(10, 0));

    [Fact]
    public async Task A_daily_job_waits_until_due_and_runs_again_the_next_day()
    {
        await using var host = new SchedulerHost(services => AddJob(services, "daily"));
        await host.StartAsync();
        Assert.Equal(TimeSpan.FromMinutes(1), await host.Time.NextTimerAsync());

        host.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(host.Runs.All);
        host.Time.Advance(TimeSpan.FromSeconds(1));

        var first = await host.Runs.NextAsync();
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero), first.Slot);
        Assert.Equal(TimeSpan.FromDays(1), await host.Time.NextTimerAsync());
        Assert.Single(host.Runs.All);

        host.Time.Advance(TimeSpan.FromDays(1));
        var second = await host.Runs.NextAsync();
        Assert.Equal(first.Slot.AddDays(1), second.Slot);
        Assert.Equal(TimeSpan.FromDays(1), await host.Time.NextTimerAsync());
        Assert.Equal(2, host.Runs.All.Count);
    }

    [Fact]
    public async Task Globally_disabled_jobs_finish_without_scheduling_or_running()
    {
        await using var host = new SchedulerHost(services => AddJob(services, "disabled"), options => options.Enabled = false);
        await host.StartAsync();
        await host.Scheduler.ExecuteTask!.WaitAsync(FailSafe);

        host.Time.Advance(TimeSpan.FromDays(2));
        Assert.Equal(0, host.Time.TimerCount);
        Assert.Empty(host.Runs.All);
    }

    [Fact]
    public async Task Disabling_one_job_keeps_the_other_job_running()
    {
        var disabledSchedules = 0;
        await using var host = new SchedulerHost(services =>
        {
            services.AddRecurringJob<ProbeJob>("disabled", _ =>
            {
                Interlocked.Increment(ref disabledSchedules);
                return Daily;
            }, RecurringJobScope.EveryInstance);
            AddJob(services, "enabled");
        }, options => options.DisabledJobs.Add("disabled"));
        await host.StartAsync();
        await host.Time.NextTimerAsync();
        host.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("enabled", (await host.Runs.NextAsync()).JobName);
        await host.Time.NextTimerAsync();
        await host.StopAsync();

        // 等所有循环停止后才断言，避免后台初始化较慢时漏掉被错误启动的禁用任务。
        Assert.Equal(0, disabledSchedules);
        Assert.Equal("enabled", Assert.Single(host.Runs.All).JobName);
    }

    [Fact]
    public async Task An_invalid_schedule_is_logged_without_stopping_other_jobs()
    {
        await using var host = new SchedulerHost(services =>
        {
            // 非法工厂先登记，确保错误日志先于健康任务的计时器信号。
            services.AddRecurringJob<ProbeJob>("invalid", _ => throw new InvalidOperationException("invalid schedule"),
                RecurringJobScope.EveryInstance);
            AddJob(services, "healthy");
        });
        await host.StartAsync();
        await host.Time.NextTimerAsync();
        host.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("healthy", (await host.Runs.NextAsync()).JobName);
        await host.Time.NextTimerAsync();

        var error = Assert.Single(host.Logs.Collector.GetSnapshot(), record => record.Level == LogLevel.Error);
        Assert.Contains("invalid", error.Message);
        Assert.IsType<InvalidOperationException>(error.Exception);
        await host.StopAsync();
        Assert.True(host.Scheduler.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Stopping_while_waiting_prevents_later_runs()
    {
        await using var host = new SchedulerHost(services => AddJob(services, "waiting"));
        await host.StartAsync();
        await host.Time.NextTimerAsync();

        await host.StopAsync();
        Assert.True(host.Scheduler.ExecuteTask!.IsCompletedSuccessfully);
        host.Time.Advance(TimeSpan.FromDays(2));
        Assert.Empty(host.Runs.All);
    }

    [Fact]
    public async Task Stopping_cancels_the_running_job_and_prevents_the_next_run()
    {
        await using var host = new SchedulerHost(services =>
            services.AddRecurringJob<CancelingJob>("running", Daily, RecurringJobScope.EveryInstance));
        await host.StartAsync();
        await host.Time.NextTimerAsync();
        host.Time.Advance(TimeSpan.FromMinutes(1));
        await host.Runs.NextAsync();

        await host.StopAsync();
        await host.Runs.Canceled.Task.WaitAsync(FailSafe);
        Assert.True(host.Scheduler.ExecuteTask!.IsCompletedSuccessfully);
        host.Time.Advance(TimeSpan.FromDays(2));
        Assert.Single(host.Runs.All);
    }

    // 到点时开关关闭：本轮跳过且不记完成；之后打开也不补跑这一轮，下一个到点才执行并记水位
    [Fact]
    public async Task A_round_due_while_switched_off_is_skipped_and_the_next_due_round_runs_after_switching_on()
    {
        var on = false;
        await using var host = new SchedulerHost(services => services
            .AddMemoryLocalLock()
            .AddRecurringJob<ProbeJob>("switched", Daily, RecurringJobScope.Cluster, _ => Volatile.Read(ref on)));
        await host.StartAsync();
        var store = host.Services.GetRequiredService<IRecurringJobStateStore>();
        await host.Time.NextTimerAsync();

        host.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(TimeSpan.FromDays(1), await host.Time.NextTimerAsync());
        Assert.Empty(host.Runs.All);
        Assert.Null(await store.GetLastCompletedSlotAsync("switched"));

        Volatile.Write(ref on, true);
        host.Time.Advance(TimeSpan.FromDays(1) - TimeSpan.FromSeconds(1));
        Assert.Empty(host.Runs.All);

        host.Time.Advance(TimeSpan.FromSeconds(1));
        var run = await host.Runs.NextAsync();
        var slot = new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(slot, run.Slot);
        await host.Time.NextTimerAsync();
        Assert.Equal(slot, await store.GetLastCompletedSlotAsync("switched"));
    }

    // 计时器会比到期时刻早几毫秒触发（毫秒取整、粗粒度单调时钟）。提前进入执行会按上一时段判为已完成而跳过，
    // 读水位期间越过时段边界后再按当前时刻排期，就整段漏掉本时段；调度循环必须补等到点
    [Fact]
    public async Task A_timer_firing_slightly_early_still_runs_the_due_slot()
    {
        var early = TimeSpan.FromMilliseconds(1);
        await using var host = new SchedulerHost(services => services
            .AddMemoryLocalLock()
            .AddRecurringJob<ProbeJob>("early", Daily, RecurringJobScope.Cluster)
            .AddSingleton<IRecurringJobStateStore>(provider =>
                new SlowStateStore((ObservedTimeProvider)provider.GetRequiredService<TimeProvider>())));
        host.Time.EarlyBy = early;
        await host.StartAsync();

        var slot = new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);
        for (var day = 0; day < 2; day++)
        {
            host.Time.Advance(await host.Time.NextTimerAsync() - early);
            Assert.Equal(early, await host.Time.NextTimerAsync());
            Assert.Equal(day, host.Runs.All.Count);

            host.Time.Advance(early);
            Assert.Equal(slot.AddDays(day), (await host.Runs.NextAsync()).Slot);
        }
    }

    private static void AddJob(IServiceCollection services, string name)
        => services.AddRecurringJob<ProbeJob>(name, Daily, RecurringJobScope.EveryInstance);

    private sealed class SchedulerHost : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        public ObservedTimeProvider Time { get; } = new();
        public RunSignals Runs { get; } = new();
        public FakeLogger<RecurringJobScheduler> Logs { get; } = new();
        public RecurringJobScheduler Scheduler { get; }
        public IServiceProvider Services => _provider;

        public SchedulerHost(Action<IServiceCollection> register, Action<BackgroundJobOptions>? configure = null)
        {
            var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
                .AddSingleton<TimeProvider>(Time)
                .AddSingleton<ILogger<RecurringJobScheduler>>(Logs)
                .AddInProcessBackgroundJobs(configure);
            services.AddScoped(_ => new ProbeJob(Runs));
            services.AddScoped(_ => new CancelingJob(Runs));
            register(services);
            _provider = services.BuildServiceProvider();
            Scheduler = _provider.GetServices<IHostedService>().OfType<RecurringJobScheduler>().Single();
        }

        public Task StartAsync() => Scheduler.StartAsync(CancellationToken.None);
        public Task StopAsync() => Scheduler.StopAsync(CancellationToken.None).WaitAsync(FailSafe);

        public async ValueTask DisposeAsync()
        {
            try { await StopAsync(); }
            finally { await _provider.DisposeAsync(); }
        }
    }

    // 只观察官方假时钟的计时器登记；.NET 10 后台初始化完成之前不能先 Advance。
    private sealed class ObservedTimeProvider : TimeProvider
    {
        private readonly FakeTimeProvider _inner = new(new DateTimeOffset(2026, 10, 3, 9, 59, 0, TimeSpan.Zero));
        private readonly Channel<TimeSpan> _timers = Channel.CreateUnbounded<TimeSpan>();
        private int _timerCount;
        public int TimerCount => Volatile.Read(ref _timerCount);

        // 计时器比登记的到期时长提前触发的量，模拟真实计时器的提前唤醒；登记信号仍报原始时长
        public TimeSpan EarlyBy { get; set; }
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _inner.CreateTimer(callback, state, dueTime > EarlyBy ? dueTime - EarlyBy : dueTime, period);
            Interlocked.Increment(ref _timerCount);
            _timers.Writer.TryWrite(dueTime);
            return timer;
        }

        public void Advance(TimeSpan duration) => _inner.Advance(duration);
        public Task<TimeSpan> NextTimerAsync() => _timers.Reader.ReadAsync().AsTask().WaitAsync(FailSafe);
    }

    // 读水位耗时 5 毫秒：提前唤醒的那一轮在读取期间越过时段边界
    private sealed class SlowStateStore(ObservedTimeProvider time) : IRecurringJobStateStore
    {
        private readonly InMemoryRecurringJobStateStore _inner = new();

        public async Task<DateTimeOffset?> GetLastCompletedSlotAsync(string jobName, CancellationToken cancellationToken = default)
        {
            var completed = await _inner.GetLastCompletedSlotAsync(jobName, cancellationToken);
            time.Advance(TimeSpan.FromMilliseconds(5));
            return completed;
        }

        public Task SetLastCompletedSlotAsync(string jobName, DateTimeOffset slot, CancellationToken cancellationToken = default)
            => _inner.SetLastCompletedSlotAsync(jobName, slot, cancellationToken);
    }

    private sealed class RunSignals
    {
        private readonly Channel<RecurringJobContext> _runs = Channel.CreateUnbounded<RecurringJobContext>();
        public ConcurrentQueue<RecurringJobContext> All { get; } = new();
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Record(RecurringJobContext context)
        {
            All.Enqueue(context);
            _runs.Writer.TryWrite(context);
        }

        public Task<RecurringJobContext> NextAsync() => _runs.Reader.ReadAsync().AsTask().WaitAsync(FailSafe);
    }

    private sealed class ProbeJob(RunSignals runs) : IRecurringJob
    {
        public Task ExecuteAsync(RecurringJobContext context, CancellationToken cancellationToken)
        {
            runs.Record(context);
            return Task.CompletedTask;
        }
    }

    private sealed class CancelingJob(RunSignals runs) : IRecurringJob
    {
        public async Task ExecuteAsync(RecurringJobContext context, CancellationToken cancellationToken)
        {
            runs.Record(context);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { if (cancellationToken.IsCancellationRequested) runs.Canceled.TrySetResult(); }
        }
    }
}
