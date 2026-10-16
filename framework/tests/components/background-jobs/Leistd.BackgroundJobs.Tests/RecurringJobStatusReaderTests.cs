using Leistd.BackgroundJobs.InProcess;
using Leistd.BackgroundJobs.InProcess.Recurring;
using Leistd.BackgroundJobs.Options;
using Leistd.BackgroundJobs.Recurring;
using Leistd.BackgroundJobs.Tests.TestDoubles;
using Leistd.Lock.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Leistd.BackgroundJobs.Tests;

/// <summary>
/// 状态读取与调度器共用启动时定下的调度事实：总开关、停用名单、排期按实际生效的那一份，任务自身的开关在读取时求值。
/// </summary>
public sealed class RecurringJobStatusReaderTests
{
    private static readonly RecurringJobSchedule Every5 = RecurringJobSchedule.Every(TimeSpan.FromMinutes(5));
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 10, 7, 30, TimeSpan.Zero);

    [Fact]
    public async Task Before_the_scheduler_starts_every_job_is_not_started()
    {
        await using var host = new Host(services => services.AddRecurringJob<CountingJob>("a", Every5, RecurringJobScope.Cluster));

        var status = Assert.Single(await host.Reader.GetStatusesAsync());
        Assert.Equal(new RecurringJobStatus("a", RecurringJobScope.Cluster, RecurringJobScheduleState.NotStarted, null, null, null), status);
    }

    [Fact]
    public async Task A_process_with_recurring_jobs_off_reports_every_job_disabled_in_process()
    {
        await using var host = new Host(
            services => services.AddRecurringJob<CountingJob>("a", Every5, RecurringJobScope.Cluster), o => o.Enabled = false);
        await host.StartAsync();

        Assert.Equal(RecurringJobScheduleState.DisabledInProcess, Assert.Single(await host.Reader.GetStatusesAsync()).State);
    }

    // 停用名单优先于任务自身的开关：已在名单里就不再求值开关
    [Fact]
    public async Task A_job_on_the_disabled_list_reports_so_without_evaluating_its_switch()
    {
        var evaluated = 0;
        await using var host = new Host(
            services => services.AddRecurringJob<CountingJob>("a", Every5, RecurringJobScope.Cluster, _ => ++evaluated > 0),
            o => o.DisabledJobs.Add("a"));
        await host.StartAsync();

        var status = Assert.Single(await host.Reader.GetStatusesAsync());
        Assert.Equal((RecurringJobScheduleState.DisabledByName, (RecurringJobSchedule?)null, 0), (status.State, status.Schedule, evaluated));
    }

    [Fact]
    public async Task A_job_whose_schedule_cannot_be_built_reports_an_invalid_schedule()
    {
        await using var host = new Host(services => services.AddRecurringJob<CountingJob>(
            "a", _ => throw new InvalidOperationException("bad schedule"), RecurringJobScope.Cluster));
        await host.StartAsync();

        var status = Assert.Single(await host.Reader.GetStatusesAsync());
        Assert.Equal((RecurringJobScheduleState.InvalidSchedule, (RecurringJobSchedule?)null, (DateTimeOffset?)null),
            (status.State, status.Schedule, status.CurrentSlot));
    }

    // 排期取调度器启动时那一份：读状态不再调用排期工厂
    [Fact]
    public async Task An_active_job_reports_its_schedule_current_slot_and_completed_slot_without_rebuilding_the_schedule()
    {
        var built = 0;
        await using var host = new Host(services =>
        {
            services.AddRecurringJob<CountingJob>("cluster", _ => { built++; return Every5; }, RecurringJobScope.Cluster);
            services.AddRecurringJob<CountingJob>("instance", Every5, RecurringJobScope.EveryInstance);
        });
        await host.StartAsync();
        var slot = new DateTimeOffset(2026, 9, 20, 10, 5, 0, TimeSpan.Zero);
        await host.Store.SetLastCompletedSlotAsync("cluster", slot.AddMinutes(-5));
        await host.Store.SetLastCompletedSlotAsync("instance", slot);

        await host.Reader.GetStatusesAsync();
        var statuses = await host.Reader.GetStatusesAsync();

        Assert.Equal(1, built);
        Assert.Equal(new RecurringJobStatus("cluster", RecurringJobScope.Cluster, RecurringJobScheduleState.Active, Every5, slot, slot.AddMinutes(-5)), statuses[0]);
        // 每副本任务不记水位，完成时段不适用
        Assert.Equal(new RecurringJobStatus("instance", RecurringJobScope.EveryInstance, RecurringJobScheduleState.Active, Every5, slot, null), statuses[1]);
    }

    // 本进程没排期不等于集群里没完成过：别的副本写下的水位照样报出来，排期相关字段为空
    [Theory]
    [InlineData(RecurringJobScheduleState.NotStarted)]
    [InlineData(RecurringJobScheduleState.DisabledInProcess)]
    [InlineData(RecurringJobScheduleState.DisabledByName)]
    [InlineData(RecurringJobScheduleState.InvalidSchedule)]
    public async Task An_unscheduled_cluster_job_still_reports_the_shared_completed_slot(RecurringJobScheduleState expected)
    {
        var evaluated = 0;
        await using var host = UnscheduledHost(expected, () => evaluated++);
        if (expected != RecurringJobScheduleState.NotStarted)
        {
            await host.StartAsync();
        }

        var completed = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
        await host.Store.SetLastCompletedSlotAsync("a", completed);

        Assert.Equal(new RecurringJobStatus("a", RecurringJobScope.Cluster, expected, null, null, completed),
            Assert.Single(await host.Reader.GetStatusesAsync()));
        Assert.Equal(0, evaluated);
    }

    [Fact]
    public async Task A_state_store_that_fails_fails_the_read_of_an_unscheduled_job()
    {
        await using var host = new Host(
            services => services.AddRecurringJob<CountingJob>("a", Every5, RecurringJobScope.Cluster),
            o => o.Enabled = false, replaceStore: true);
        await host.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Reader.GetStatusesAsync());
    }

    [Fact]
    public async Task The_switch_is_read_at_each_call()
    {
        var on = false;
        await using var host = new Host(services => services.AddRecurringJob<CountingJob>("a", Every5, RecurringJobScope.Cluster, _ => on));
        await host.StartAsync();

        Assert.Equal(RecurringJobScheduleState.SwitchedOff, Assert.Single(await host.Reader.GetStatusesAsync()).State);
        on = true;
        Assert.Equal(RecurringJobScheduleState.Active, Assert.Single(await host.Reader.GetStatusesAsync()).State);
    }

    // 判断不了不能显示成“已关闭”或“从未完成”：整次读取失败，由消费方显示无法判断
    [Fact]
    public async Task A_switch_that_throws_fails_the_read()
    {
        await using var host = new Host(services => services.AddRecurringJob<CountingJob>(
            "a", Every5, RecurringJobScope.Cluster, _ => throw new InvalidOperationException("switch unavailable")));
        await host.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Reader.GetStatusesAsync());
    }

    [Fact]
    public async Task A_state_store_that_fails_fails_the_read()
    {
        await using var host = new Host(
            services => services.AddRecurringJob<CountingJob>("a", Every5, RecurringJobScope.Cluster),
            replaceStore: true);
        await host.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Reader.GetStatusesAsync());
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        await using var host = new Host(services => services.AddRecurringJob<CountingJob>("a", Every5, RecurringJobScope.Cluster));
        await host.StartAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Reader.GetStatusesAsync(new CancellationToken(canceled: true)));
    }

    private static Host UnscheduledHost(RecurringJobScheduleState state, Action evaluate)
    {
        Func<IServiceProvider, bool> isEnabled = _ => { evaluate(); return true; };
        return state switch
        {
            RecurringJobScheduleState.InvalidSchedule => new Host(services => services.AddRecurringJob<CountingJob>(
                "a", _ => throw new InvalidOperationException("bad schedule"), RecurringJobScope.Cluster, isEnabled)),
            RecurringJobScheduleState.DisabledInProcess => new Host(
                services => services.AddRecurringJob<CountingJob>("a", Every5, RecurringJobScope.Cluster, isEnabled), o => o.Enabled = false),
            RecurringJobScheduleState.DisabledByName => new Host(
                services => services.AddRecurringJob<CountingJob>("a", Every5, RecurringJobScope.Cluster, isEnabled), o => o.DisabledJobs.Add("a")),
            _ => new Host(services => services.AddRecurringJob<CountingJob>("a", Every5, RecurringJobScope.Cluster, isEnabled)),
        };
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IHostedService _scheduler;

        public Host(Action<IServiceCollection> register, Action<BackgroundJobOptions>? configure = null, bool replaceStore = false)
        {
            var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
                .AddMemoryLocalLock()
                .AddSingleton(new JobLog())
                .AddInProcessBackgroundJobs(configure);
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new FakeTimeProvider(Now)));
            if (replaceStore)
            {
                services.Replace(ServiceDescriptor.Singleton<IRecurringJobStateStore, FailingStore>());
            }

            register(services);
            _provider = services.BuildServiceProvider();
            _scheduler = _provider.GetServices<IHostedService>().OfType<RecurringJobScheduler>().Single();
        }

        public IRecurringJobStatusReader Reader => _provider.GetRequiredService<IRecurringJobStatusReader>();

        public IRecurringJobStateStore Store => _provider.GetRequiredService<IRecurringJobStateStore>();

        public async Task StartAsync()
        {
            await _scheduler.StartAsync(CancellationToken.None);
            // .NET 10 的 BackgroundService 在后台开始执行，调度事实随后才登记：等它登记完再读
            var plan = _provider.GetRequiredService<RecurringJobPlan>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (plan.Jobs is null)
            {
                await Task.Delay(10, timeout.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await _scheduler.StopAsync(CancellationToken.None); }
            finally { await _provider.DisposeAsync(); }
        }
    }

    private sealed class FailingStore : IRecurringJobStateStore
    {
        public Task<DateTimeOffset?> GetLastCompletedSlotAsync(string jobName, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store unavailable");

        public Task SetLastCompletedSlotAsync(string jobName, DateTimeOffset slot, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store unavailable");
    }
}
