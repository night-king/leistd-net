using Leistd.BackgroundJobs.InProcess;
using Leistd.BackgroundJobs.InProcess.Recurring;
using Leistd.BackgroundJobs.Recurring;
using Leistd.BackgroundJobs.Tests.TestDoubles;
using Leistd.Lock.Abstractions;
using Leistd.Lock.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Leistd.BackgroundJobs.Tests;

/// <summary>
/// 任务自身的开关：每轮执行前在本轮作用域里求值，关闭时跳过且不记完成；开关与任务读同一份配置快照。
/// </summary>
public sealed class RecurringJobSwitchTests : IDisposable
{
    private const string Section = "Test:Switch";
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 20, 10, 7, 30, TimeSpan.Zero));
    private readonly IConfigurationRoot _configuration;
    private readonly ServiceProvider _provider;
    private readonly JobLog _log = new();
    private readonly FakeLogger<RecurringJobRunner> _logs = new();
    private bool _switch = true;

    public RecurringJobSwitchTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"{Section}:Enabled"] = "true", [$"{Section}:Days"] = "90" })
            .Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(_configuration)
            .AddMemoryLocalLock()
            .AddInProcessBackgroundJobs()
            .AddRecurringJob<CountingJob>("test.switched", RecurringJobSchedule.Every(TimeSpan.FromMinutes(5)),
                RecurringJobScope.Cluster, isEnabled: _ => _switch)
            .AddRecurringJob<CountingJob>("test.switched-instance", RecurringJobSchedule.Every(TimeSpan.FromMinutes(5)),
                RecurringJobScope.EveryInstance, isEnabled: _ => _switch)
            .AddRecurringJob<CountingJob>("test.throwing-switch", RecurringJobSchedule.Every(TimeSpan.FromMinutes(5)),
                RecurringJobScope.Cluster, isEnabled: _ => throw new InvalidOperationException("switch unavailable"))
            .AddRecurringJob<SnapshotJob>("test.snapshot", RecurringJobSchedule.Every(TimeSpan.FromMinutes(5)),
                RecurringJobScope.Cluster, isEnabled: sp => sp.GetRequiredService<IOptionsSnapshot<SwitchOptions>>().Value.Enabled);
        services.AddOptions<SwitchOptions>().BindConfiguration(Section);
        services.AddSingleton(_log);
        services.AddSingleton<ILogger<RecurringJobRunner>>(_logs);
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_time));
        // 取锁时把配置热改成“关闭且清空天数”：模拟预检之后、执行之前的合法设置更新
        var memoryLock = services.Last(d => d.ServiceType == typeof(IDistributedLock));
        services.Remove(memoryLock);
        services.AddSingleton<IDistributedLock>(sp => new FlippingLock(
            (IDistributedLock)(memoryLock.ImplementationInstance ?? memoryLock.ImplementationFactory?.Invoke(sp)
                ?? ActivatorUtilities.CreateInstance(sp, memoryLock.ImplementationType!)),
            _configuration));
        _provider = services.BuildServiceProvider();
    }

    public void Dispose() => _provider.Dispose();

    private Task<RecurringJobRunOutcome> RunAsync(string name)
    {
        var definition = _provider.GetServices<RecurringJobDefinition>().Single(d => d.Name == name);
        return _provider.GetRequiredService<RecurringJobRunner>()
            .RunOnceAsync(definition, definition.ScheduleFactory(_provider), CancellationToken.None);
    }

    private Task<DateTimeOffset?> CompletedAsync(string name)
        => _provider.GetRequiredService<IRecurringJobStateStore>().GetLastCompletedSlotAsync(name);

    [Fact]
    public async Task A_switched_off_job_is_skipped_without_recording_the_slot_and_resumes_when_switched_on()
    {
        _switch = false;
        Assert.Equal(RecurringJobRunOutcome.SkippedDisabled, await RunAsync("test.switched"));
        Assert.Empty(_log.Runs);
        Assert.Null(await CompletedAsync("test.switched"));

        _switch = true;
        Assert.Equal(RecurringJobRunOutcome.Completed, await RunAsync("test.switched"));
        Assert.Single(_log.Runs);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 10, 5, 0, TimeSpan.Zero), await CompletedAsync("test.switched"));
    }

    [Fact]
    public async Task A_switched_off_every_instance_job_is_skipped_too()
    {
        _switch = false;
        Assert.Equal(RecurringJobRunOutcome.SkippedDisabled, await RunAsync("test.switched-instance"));
        Assert.Empty(_log.Runs);
    }

    // 判断不了不等于关闭：按失败处理，不执行、不记完成
    [Fact]
    public async Task A_switch_that_throws_fails_the_round_instead_of_counting_as_off()
    {
        Assert.Equal(RecurringJobRunOutcome.Failed, await RunAsync("test.throwing-switch"));
        Assert.Empty(_log.Runs);
        Assert.Null(await CompletedAsync("test.throwing-switch"));
    }

    // 预检看到“开启、90 天”，取锁时设置被改成“关闭、天数清空”：本轮按开启的那份配置做完，不会拿到清空的天数
    [Fact]
    public async Task A_round_admitted_as_on_keeps_its_configuration_when_the_setting_changes_mid_round()
    {
        Assert.Equal(RecurringJobRunOutcome.Completed, await RunAsync("test.snapshot"));
        Assert.Single(_log.Runs);
        Assert.Equal(90, SnapshotJob.LastDays);

        // 下一轮的新作用域读到关闭
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(RecurringJobRunOutcome.SkippedDisabled, await RunAsync("test.snapshot"));
        Assert.Single(_log.Runs);
    }

    // 只在切换时记日志，持续关闭不刷屏
    [Fact]
    public async Task Switching_off_and_on_is_logged_once_each()
    {
        _switch = false;
        await RunAsync("test.switched");
        await RunAsync("test.switched");
        _switch = true;
        await RunAsync("test.switched");
        await RunAsync("test.switched");

        var switches = _logs.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Information).Select(r => r.Message).ToList();
        Assert.Equal(2, switches.Count);
        Assert.Contains("switched off", switches[0], StringComparison.Ordinal);
        Assert.Contains("switched on again", switches[1], StringComparison.Ordinal);
    }

    private sealed class SwitchOptions
    {
        public bool Enabled { get; set; }

        public int? Days { get; set; }
    }

    // 按快照读配置的任务：开启时天数必填，读到清空的天数就会在 .Value 上抛出
    private sealed class SnapshotJob(IOptionsSnapshot<SwitchOptions> options, JobLog log) : IRecurringJob
    {
        public static int? LastDays { get; private set; }

        public Task ExecuteAsync(RecurringJobContext context, CancellationToken cancellationToken)
        {
            LastDays = options.Value.Days!.Value;
            log.Runs.Add(context);
            return Task.CompletedTask;
        }
    }

    private sealed class FlippingLock(IDistributedLock inner, IConfigurationRoot configuration) : IDistributedLock
    {
        public Task<ILockHandle> LockAsync(string key, CancellationToken cancellationToken = default)
            => inner.LockAsync(key, cancellationToken);

        public Task<ILockHandle?> TryLockAsync(string key, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (key.EndsWith("test.snapshot", StringComparison.Ordinal))
            {
                configuration[$"{Section}:Enabled"] = "false";
                configuration[$"{Section}:Days"] = null;
            }

            return inner.TryLockAsync(key, timeout, cancellationToken);
        }
    }
}
