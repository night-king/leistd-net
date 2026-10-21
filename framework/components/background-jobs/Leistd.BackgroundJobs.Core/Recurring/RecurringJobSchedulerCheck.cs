using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Leistd.BackgroundJobs.Recurring;

// 只告警、不抛出：迁移作业等进程可能有意不跑周期任务，但它们也会注册同一批组件
internal sealed class RecurringJobSchedulerCheck(
    IServiceProvider serviceProvider,
    IEnumerable<RecurringJobDefinition> definitions,
    ILogger<RecurringJobSchedulerCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (serviceProvider.GetService<RecurringJobSchedulerMarker>() is null)
        {
            logger.LogWarning(
                "{Count} recurring job(s) are registered ({Names}) but no scheduler is; they will never run. " +
                "Register a scheduler such as AddInProcessBackgroundJobs().",
                definitions.Count(),
                string.Join(", ", definitions.Select(definition => definition.Name)));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
