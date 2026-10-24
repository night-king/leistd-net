using System.Diagnostics;
using Leistd.AmbientContext;
using Leistd.BackgroundJobs.InProcess.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Leistd.BackgroundJobs.InProcess.Queues;

// 队列消费者。单项失败只记日志：ExecuteAsync 一旦抛出，后续工作项都不再执行。
// 停机时先关写入口；正在执行的一项收到取消令牌，尚未开始的项被丢弃。
internal sealed class BackgroundTaskQueueWorker(
    BackgroundTaskQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<BackgroundTaskQueueWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken))
            {
                await RunAsync(item, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task RunAsync(QueuedWorkItem item, CancellationToken stoppingToken)
    {
        // 工作项在自己的 Activity 里执行（含失败日志）：有入队时的链路就接上它，没有就是新的根链路，
        // 不继承消费循环里偶然残留的上下文。结束后还原消费者原来的 Activity。
        var previous = Activity.Current;
        Activity.Current = null;
        var activity = BackgroundJobDiagnostics.StartActivity("Leistd.BackgroundWorkItem", item.Parent);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            using var restored = item.Context is { } context
                ? scope.ServiceProvider.GetRequiredService<IAmbientContext>().Restore(context)
                : null;
            try
            {
                await item.WorkItem(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                // 在还原后的上下文里记：失败日志带着入队时的主体、租户与关联标识
                logger.LogError(exception, "A background work item failed and was discarded.");
            }
        }
        catch (Exception exception)
        {
            // 作用域或上下文还原本身失败：工作项没有执行
            logger.LogError(exception, "A background work item could not be started and was discarded.");
        }
        finally
        {
            activity.Stop();
            Activity.Current = previous;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        await base.StopAsync(cancellationToken);
    }
}
