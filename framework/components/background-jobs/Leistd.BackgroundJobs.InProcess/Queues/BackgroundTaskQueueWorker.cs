using System.Diagnostics;
using Leistd.AmbientContext;
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
        var activity = StartActivity(item.Parent);
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

    // 有监听者（如 OpenTelemetry）时经 ActivitySource 创建，工作项进入采样与导出；
    // 没有监听者时 StartActivity 返回 null，仍手动建一个 Activity，让日志里的 TraceId 延续入队时的链路。
    private static Activity StartActivity(ActivityContext? parent)
    {
        if (parent is { } context)
        {
            if (Source.StartActivity(ActivityName, ActivityKind.Internal, context) is { } listened)
            {
                return listened;
            }

            var activity = new Activity(ActivityName).SetParentId(context.TraceId, context.SpanId, context.TraceFlags);
            activity.TraceStateString = context.TraceState;
            return activity.Start();
        }

        return Source.StartActivity(ActivityName, ActivityKind.Internal, default(ActivityContext))
            ?? new Activity(ActivityName).Start();
    }

    // 后台工作项的 ActivitySource 名；OpenTelemetry 按它订阅（组件文档写明）
    internal const string ActivitySourceName = "Leistd.BackgroundJobs";

    private const string ActivityName = "Leistd.BackgroundWorkItem";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        await base.StopAsync(cancellationToken);
    }
}
