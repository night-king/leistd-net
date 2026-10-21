using Leistd.EventBus.EventHandlers;
using Leistd.EventBus.Events;
using Leistd.EventBus.Local;
using Microsoft.Extensions.DependencyInjection;

using Xunit;
using Leistd.EventBus.Abstractions;

namespace Leistd.EventBus.Tests;

/// <summary>一个处理器失败不阻断其余处理器；失败被聚合后上抛。</summary>
/// <remarks>
/// 若用裸 <c>foreach + await</c>，第一个抛异常的处理器会让后面的处理器
/// <b>根本不执行</b>。而处理器之间彼此无依赖——"发通知"失败不该让"失效缓存"也不跑，
/// 且执行顺序取决于 DI 注册顺序，于是"哪些副作用生效了"随注册顺序变化。
/// </remarks>
public class LocalEventBusHandlerFailureTests
{
    private sealed class OrderPlaced : LocalEvent
    {
    }

    private sealed class Recorder
    {
        public List<string> Executed { get; } = [];
    }

    private sealed class FailingHandler(Recorder recorder) : IEventHandler<OrderPlaced>
    {
        public Task HandleAsync(OrderPlaced @event, CancellationToken cancellationToken = default)
        {
            recorder.Executed.Add("failing");
            throw new InvalidOperationException("handler-1 failed");
        }
    }

    private sealed class SecondFailingHandler(Recorder recorder) : IEventHandler<OrderPlaced>
    {
        public Task HandleAsync(OrderPlaced @event, CancellationToken cancellationToken = default)
        {
            recorder.Executed.Add("failing-2");
            throw new TimeoutException("handler-2 failed");
        }
    }

    private sealed class SucceedingHandler(Recorder recorder) : IEventHandler<OrderPlaced>
    {
        public Task HandleAsync(OrderPlaced @event, CancellationToken cancellationToken = default)
        {
            recorder.Executed.Add("succeeding");
            return Task.CompletedTask;
        }
    }

    private static (IServiceProvider Provider, Recorder Recorder) Build(params Type[] handlerTypes)
    {
        var recorder = new Recorder();
        var services = new ServiceCollection()
            .AddLocalEventBus()
            .AddSingleton(recorder);

        foreach (var handlerType in handlerTypes)
        {
            services.AddTransient(typeof(IEventHandler<OrderPlaced>), handlerType);
        }

        return (services.BuildServiceProvider(), recorder);
    }

    [Fact]
    public async Task A_failing_handler_does_not_prevent_the_remaining_handlers()
    {
        var (provider, recorder) = Build(typeof(FailingHandler), typeof(SucceedingHandler));
        var bus = provider.GetRequiredService<ILocalEventBus>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => bus.PublishAsync(new OrderPlaced()));

        Assert.Equal(["failing", "succeeding"], recorder.Executed);
    }

    [Fact]
    public async Task A_single_failure_is_rethrown_with_its_original_type()
    {
        var (provider, _) = Build(typeof(FailingHandler), typeof(SucceedingHandler));
        var bus = provider.GetRequiredService<ILocalEventBus>();

        // 原类型上抛，调用方按类型 catch 的代码不受聚合改造影响
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bus.PublishAsync(new OrderPlaced()));

        Assert.Equal("handler-1 failed", exception.Message);
    }

    [Fact]
    public async Task Multiple_failures_are_aggregated_and_every_handler_still_runs()
    {
        var (provider, recorder) = Build(
            typeof(FailingHandler), typeof(SucceedingHandler), typeof(SecondFailingHandler));
        var bus = provider.GetRequiredService<ILocalEventBus>();

        var aggregate = await Assert.ThrowsAsync<AggregateException>(
            () => bus.PublishAsync(new OrderPlaced()));

        Assert.Equal(3, recorder.Executed.Count);
        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.Contains(aggregate.InnerExceptions, e => e is InvalidOperationException);
        Assert.Contains(aggregate.InnerExceptions, e => e is TimeoutException);
    }

    [Fact]
    public async Task Publishing_without_handlers_succeeds()
    {
        var (provider, _) = Build();
        var bus = provider.GetRequiredService<ILocalEventBus>();

        await bus.PublishAsync(new OrderPlaced());
    }
}
