using System.Diagnostics;
using System.Threading.Channels;
using Leistd.AmbientContext;
using Leistd.BackgroundJobs.InProcess.Options;
using Leistd.BackgroundJobs.Queues;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Leistd.BackgroundJobs.InProcess.Queues;

// 有界通道：满了就等（QueueAsync）或返回 false（TryQueue），不丢也不抛。
// 入队时捕获环境上下文与当前链路；未注册环境上下文（没有安全组件）时工作项在空上下文里执行。
internal sealed class BackgroundTaskQueue(
    IServiceProvider rootProvider,
    IOptions<InProcessBackgroundJobOptions> options) : IBackgroundTaskQueue
{
    private readonly Channel<QueuedWorkItem> _channel = Channel.CreateBounded<QueuedWorkItem>(
        new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    internal ChannelReader<QueuedWorkItem> Reader => _channel.Reader;

    public ValueTask QueueAsync(
        Func<IServiceProvider, CancellationToken, ValueTask> workItem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        return _channel.Writer.WriteAsync(Capture(workItem), cancellationToken);
    }

    public bool TryQueue(Func<IServiceProvider, CancellationToken, ValueTask> workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        return _channel.Writer.TryWrite(Capture(workItem));
    }

    internal void Complete() => _channel.Writer.TryComplete();

    private QueuedWorkItem Capture(Func<IServiceProvider, CancellationToken, ValueTask> workItem)
        => new(workItem, rootProvider.GetService<IAmbientContext>()?.Capture(), Activity.Current?.Context);
}

internal sealed record QueuedWorkItem(
    Func<IServiceProvider, CancellationToken, ValueTask> WorkItem,
    AmbientContextSnapshot? Context,
    ActivityContext? Parent);
