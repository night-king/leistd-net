using Leistd.BackgroundJobs.InProcess;
using Leistd.BackgroundJobs.Recurring;
using Leistd.BackgroundJobs.Tests.TestDoubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Leistd.BackgroundJobs.Tests;

/// <summary>登记了周期任务却没有调度器时，任务永远不跑且不报错；启动时要在日志里留下这件事。</summary>
/// <remarks>只告警不抛出：迁移作业等进程可能有意不跑周期任务，但注册的是同一批组件。</remarks>
public class RecurringJobSchedulerCheckTests
{
    private static readonly RecurringJobSchedule Hourly = RecurringJobSchedule.Every(TimeSpan.FromHours(1));

    [Fact]
    public async Task Jobs_without_a_scheduler_are_reported_at_startup()
    {
        var (provider, logs) = Build(withScheduler: false);
        await using var _ = provider;

        await StartAsync(provider);

        var warning = Assert.Single(logs.GetSnapshot(), record => record.Level == LogLevel.Warning);
        Assert.Contains("test.counting", warning.Message);
    }

    [Fact]
    public async Task Jobs_with_a_scheduler_start_quietly()
    {
        var (provider, logs) = Build(withScheduler: true);
        await using var _ = provider;

        // 只启动检查本身：启动调度器会真的开始排期执行任务
        foreach (var check in provider.GetServices<IHostedService>()
                     .Where(service => service.GetType().Name == "RecurringJobSchedulerCheck"))
        {
            await check.StartAsync(CancellationToken.None);
        }

        Assert.DoesNotContain(logs.GetSnapshot(), record => record.Level == LogLevel.Warning);
    }

    private static (ServiceProvider Provider, FakeLogCollector Logs) Build(bool withScheduler)
    {
        var services = new ServiceCollection();
        services.AddFakeLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddRecurringJob<CountingJob>("test.counting", Hourly, RecurringJobScope.EveryInstance);
        if (withScheduler)
        {
            services.AddInProcessBackgroundJobs();
        }

        var provider = services.BuildServiceProvider();
        return (provider, provider.GetFakeLogCollector());
    }

    private static async Task StartAsync(IServiceProvider provider)
    {
        foreach (var hostedService in provider.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }
    }
}
