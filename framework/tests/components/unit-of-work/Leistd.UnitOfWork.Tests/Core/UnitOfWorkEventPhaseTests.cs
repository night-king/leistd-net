using Leistd.DependencyInjection.DynamicProxy.Registration;
using Leistd.EventBus.Abstractions;
using Leistd.EventBus.EventHandlers;
using Leistd.EventBus.Events;
using Leistd.EventBus.Local;
using Leistd.UnitOfWork.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Leistd.UnitOfWork.Tests.Core;

/// <summary>事件处理器的阶段归属：每个处理器每个事件<b>恰好执行一次</b>。</summary>
/// <remarks>
/// <para>这一组钉的是"缺省阶段"。<c>CompleteAsync</c> 对每个事件发布两趟
/// （BeforeCommit 一趟、AfterCommit 一趟），靠拦截器挡掉不属于当前阶段的那趟。
/// 若只有带 <c>[UnitOfWorkEventHandler]</c> 的处理器才被织入，不带特性的处理器
/// 完全不被代理，两趟都执行——同一个副作用做两遍，且第二遍还落在"已完成的工作单元"
/// 之外，走仓储会另开连接自动提交，稳定产生重复数据。</para>
/// <para>断言的是<b>执行次数与所处阶段</b>，不是内部字段：次数与阶段才是调用方能观察到的后果。</para>
/// </remarks>
public sealed class UnitOfWorkEventPhaseTests
{
    [Fact]
    public async Task Unannotated_handler_runs_once_after_the_commit()
    {
        await using var provider = Build();
        var recorder = provider.GetRequiredService<PhaseRecorder>();

        var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin();
        uow.AddPendingEvents([new ProbeEvent()]);
        await uow.CompleteAsync();
        uow.Dispose();

        Assert.Equal([UnitOfWorkPhase.AfterCommit], recorder.Unannotated);
    }

    [Fact]
    public async Task Annotated_handlers_each_run_once_in_their_own_phase()
    {
        await using var provider = Build();
        var recorder = provider.GetRequiredService<PhaseRecorder>();

        var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin();
        uow.AddPendingEvents([new ProbeEvent()]);
        await uow.CompleteAsync();
        uow.Dispose();

        Assert.Equal([UnitOfWorkPhase.BeforeCommit], recorder.BeforeCommit);
        Assert.Equal([UnitOfWorkPhase.AfterCommit], recorder.AfterCommit);
    }

    [Fact]
    public async Task Unannotated_handler_runs_once_when_published_without_a_unit_of_work()
    {
        await using var provider = Build();
        var recorder = provider.GetRequiredService<PhaseRecorder>();

        await provider.GetRequiredService<ILocalEventBus>().PublishAsync(new ProbeEvent());

        Assert.Single(recorder.Unannotated);
        Assert.Null(recorder.Unannotated[0]);
    }

    /// <summary>
    /// 工作单元外发布时 <c>BeforeCommit</c> 处理器不执行：该阶段的契约是"我的异常能回滚事务"，
    /// 而这条路的发布点在数据已落库之后，契约无从兑现。带着假承诺执行比跳过更危险。
    /// </summary>
    [Fact]
    public async Task BeforeCommit_handler_is_skipped_when_published_without_a_unit_of_work()
    {
        await using var provider = Build();
        var recorder = provider.GetRequiredService<PhaseRecorder>();

        await provider.GetRequiredService<ILocalEventBus>().PublishAsync(new ProbeEvent());

        Assert.Empty(recorder.BeforeCommit);
        Assert.Single(recorder.AfterCommit);
    }

    /// <summary>
    /// <c>AfterCommit</c> 阶段没有环境工作单元，这是<b>契约</b>而非缺陷：事务已落定，
    /// 此阶段的写入必须自己提交。挂进一个已完成的工作单元将永远等不到 SaveChanges，
    /// 变更会随作用域释放静默丢弃。
    /// </summary>
    [Fact]
    public async Task AfterCommit_phase_has_no_ambient_unit_of_work()
    {
        await using var provider = Build();
        var recorder = provider.GetRequiredService<PhaseRecorder>();

        var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin();
        uow.AddPendingEvents([new ProbeEvent()]);
        await uow.CompleteAsync();
        uow.Dispose();

        Assert.Equal([true], recorder.BeforeCommitSawUnitOfWork);
        Assert.Equal([false], recorder.AfterCommitSawUnitOfWork);
    }

    /// <summary>嵌套独立工作单元不得改坏外层的阶段。</summary>
    /// <remarks>
    /// BeforeCommit 处理器里开一个 <c>requiresNew</c> 的工作单元是真实形态，它自己也会走一遍阶段。
    /// 若阶段在退出时被清空而不是还原，外层剩余处理器会读到 <see langword="null"/>——
    /// AfterCommit 处理器提前执行、BeforeCommit 处理器被跳过。
    /// </remarks>
    [Fact]
    public async Task Nested_unit_of_work_does_not_clobber_the_outer_phase()
    {
        await using var provider = Build();
        var recorder = provider.GetRequiredService<PhaseRecorder>();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        var outer = manager.Begin(requiresNew: true);
        outer.AddPendingEvents([new NestingEvent()]);
        await outer.CompleteAsync();
        outer.Dispose();

        // 第一个 BeforeCommit 处理器内部跑完一整个独立工作单元后，外层阶段仍须是 BeforeCommit
        Assert.Equal(
            [UnitOfWorkPhase.BeforeCommit, UnitOfWorkPhase.BeforeCommit],
            recorder.NestingObserved);
    }

    /// <summary>嵌套独立工作单元的 <c>AfterCommit</c> 阶段同样没有环境工作单元。</summary>
    /// <remarks>
    /// 管理器只跳过"已完成"的工作单元、随后顺 <c>Outer</c> 链返回外层，因此嵌套场景下
    /// 若不显式置空，处理器会拿到<b>外层</b>工作单元：写入并入外层事务，既不是契约声明的
    /// 独立提交，也不随本工作单元回滚。
    /// </remarks>
    [Fact]
    public async Task Nested_after_commit_still_has_no_ambient_unit_of_work()
    {
        await using var provider = Build();
        var recorder = provider.GetRequiredService<PhaseRecorder>();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        using var outer = manager.Begin(requiresNew: true);
        var inner = manager.Begin(requiresNew: true);
        var ambient = provider.GetRequiredService<IAmbientUnitOfWork>();
        var beforeCompletion = ambient.Get();
        Assert.Same(inner, beforeCompletion);
        inner.AddPendingEvents([new ProbeEvent()]);
        await inner.CompleteAsync();
        Assert.Same(beforeCompletion, ambient.Get());
        inner.Dispose();
        Assert.Same(outer, ambient.Get());

        Assert.Equal([false], recorder.AfterCommitSawUnitOfWork);
        // 还原：外层在内层结束后仍是当前工作单元
        Assert.Same(outer, manager.Current);
    }

    /// <summary>活动工作单元内<b>直接</b>发布的事件，同样按阶段调度，而不是立即分发。</summary>
    /// <remarks>
    /// 业务代码在 <c>[UnitOfWork]</c> 方法里调 <c>ILocalEventBus.PublishAsync</c> 是常见写法。
    /// 若立即分发：未标注与 AfterCommit 处理器在提交前就执行（事务随后回滚也收不回），
    /// 而 BeforeCommit 处理器因为此刻没有阶段上下文反被当成"工作单元外发布"跳过——两头都错。
    /// 推迟之后每个处理器仍在自己声明的阶段恰好执行一次。
    /// </remarks>
    [Fact]
    public async Task Direct_publish_inside_an_active_unit_of_work_is_scheduled_by_phase()
    {
        await using var provider = Build();
        var recorder = provider.GetRequiredService<PhaseRecorder>();
        var bus = provider.GetRequiredService<ILocalEventBus>();

        var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true);
        await bus.PublishAsync(new ProbeEvent());

        // 发布调用返回时还什么都没跑：事件已进入待发队列
        Assert.Empty(recorder.Unannotated);
        Assert.Empty(recorder.BeforeCommit);

        await uow.CompleteAsync();
        uow.Dispose();

        Assert.Equal([UnitOfWorkPhase.BeforeCommit], recorder.BeforeCommit);
        Assert.Equal([UnitOfWorkPhase.AfterCommit], recorder.Unannotated);
        Assert.Equal([UnitOfWorkPhase.AfterCommit], recorder.AfterCommit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_after_commit_failure_does_not_skip_later_events_or_lose_its_exception(bool cancelled)
    {
        await using var provider = Build();
        var recorder = provider.GetRequiredService<PhaseRecorder>();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        using var outer = manager.Begin(requiresNew: true);
        using var inner = manager.Begin(requiresNew: true);
        var ambient = provider.GetRequiredService<IAmbientUnitOfWork>();
        var beforeCompletion = ambient.Get();
        Assert.Same(inner, beforeCompletion);
        Exception failure = cancelled
            ? new OperationCanceledException("side effect cancelled")
            : new InvalidOperationException("side effect unavailable");
        inner.AddPendingEvents([new FailingAfterCommitEvent(failure), new ProbeEvent()]);

        var observed = await Assert.ThrowsAnyAsync<Exception>(() => inner.CompleteAsync());

        Assert.Same(failure, observed);
        Assert.Same(beforeCompletion, ambient.Get());
        Assert.True(inner.IsCompleted);
        Assert.Equal([UnitOfWorkPhase.AfterCommit], recorder.Unannotated);
        Assert.Equal([UnitOfWorkPhase.AfterCommit], recorder.AfterCommit);
        Assert.Null(UnitOfWorkContext.CurrentPhase);
        Assert.Same(outer, manager.Current);
        var logs = provider.GetFakeLogCollector().GetSnapshot();
        Assert.Contains(logs, record => record.Level == LogLevel.Error &&
            record.Message.Contains("committed, but after-commit", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, record =>
            record.Message.Contains("commit failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Multiple_after_commit_failures_are_aggregated_after_all_events_run()
    {
        await using var provider = Build();
        var recorder = provider.GetRequiredService<PhaseRecorder>();
        using var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin();
        var first = new InvalidOperationException("first side effect unavailable");
        var second = new IOException("second side effect unavailable");
        uow.AddPendingEvents([
            new FailingAfterCommitEvent(first), new ProbeEvent(),
            new FailingAfterCommitEvent(second), new ProbeEvent()]);

        var observed = await Assert.ThrowsAsync<AggregateException>(() => uow.CompleteAsync());

        Assert.Equal(2, observed.InnerExceptions.Count);
        Assert.Same(first, observed.InnerExceptions[0]);
        Assert.Same(second, observed.InnerExceptions[1]);
        Assert.True(uow.IsCompleted);
        Assert.Equal([UnitOfWorkPhase.AfterCommit, UnitOfWorkPhase.AfterCommit], recorder.Unannotated);
        Assert.Null(UnitOfWorkContext.CurrentPhase);
    }

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFakeLogging();
        services.AddLocalEventBus();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUnitOfWork();
        services.AddSingleton<PhaseRecorder>();
        services.AddTransient<IEventHandler<ProbeEvent>, UnannotatedHandler>();
        services.AddTransient<IEventHandler<ProbeEvent>, BeforeCommitHandler>();
        services.AddTransient<IEventHandler<ProbeEvent>, AfterCommitHandler>();
        services.AddTransient<IEventHandler<NestingEvent>, NestingOuterFirstHandler>();
        services.AddTransient<IEventHandler<NestingEvent>, NestingOuterSecondHandler>();
        services.AddTransient<IEventHandler<FailingAfterCommitEvent>, FailingAfterCommitHandler>();
        return (ServiceProvider)new DynamicProxyServiceRegistrationCallbackFactory()
            .CreateServiceProvider(services);
    }

    public sealed class ProbeEvent : LocalEvent;

    public sealed class FailingAfterCommitEvent(Exception failure) : LocalEvent
    {
        public Exception Failure { get; } = failure;
    }

    public sealed class FailingAfterCommitHandler : IEventHandler<FailingAfterCommitEvent>
    {
        public Task HandleAsync(FailingAfterCommitEvent @event, CancellationToken cancellationToken = default)
            => Task.FromException(@event.Failure);
    }

    public sealed class NestingEvent : LocalEvent;

    /// <summary>按处理器记录"执行了几次、每次处在哪个阶段、当时有没有环境工作单元"</summary>
    public sealed class PhaseRecorder
    {
        public List<UnitOfWorkPhase?> Unannotated { get; } = [];

        public List<UnitOfWorkPhase?> BeforeCommit { get; } = [];

        public List<UnitOfWorkPhase?> AfterCommit { get; } = [];

        public List<bool> BeforeCommitSawUnitOfWork { get; } = [];

        public List<bool> AfterCommitSawUnitOfWork { get; } = [];

        /// <summary>嵌套用例里两个外层 BeforeCommit 处理器各自观察到的阶段。</summary>
        public List<UnitOfWorkPhase?> NestingObserved { get; } = [];
    }

    /// <summary>不带 <c>[UnitOfWorkEventHandler]</c>：应等同 <c>AfterCommit</c></summary>
    public sealed class UnannotatedHandler(PhaseRecorder recorder) : IEventHandler<ProbeEvent>
    {
        public Task HandleAsync(ProbeEvent @event, CancellationToken cancellationToken = default)
        {
            recorder.Unannotated.Add(UnitOfWorkContext.CurrentPhase);
            return Task.CompletedTask;
        }
    }

    [UnitOfWorkEventHandler(Phase = UnitOfWorkPhase.BeforeCommit)]
    public sealed class BeforeCommitHandler(PhaseRecorder recorder, IUnitOfWorkManager manager)
        : IEventHandler<ProbeEvent>
    {
        public Task HandleAsync(ProbeEvent @event, CancellationToken cancellationToken = default)
        {
            recorder.BeforeCommit.Add(UnitOfWorkContext.CurrentPhase);
            recorder.BeforeCommitSawUnitOfWork.Add(manager.Current is not null);
            return Task.CompletedTask;
        }
    }

    [UnitOfWorkEventHandler(Phase = UnitOfWorkPhase.AfterCommit)]
    public sealed class AfterCommitHandler(PhaseRecorder recorder, IUnitOfWorkManager manager)
        : IEventHandler<ProbeEvent>
    {
        public Task HandleAsync(ProbeEvent @event, CancellationToken cancellationToken = default)
        {
            recorder.AfterCommit.Add(UnitOfWorkContext.CurrentPhase);
            recorder.AfterCommitSawUnitOfWork.Add(manager.Current is not null);
            return Task.CompletedTask;
        }
    }

    /// <summary>外层第一个处理器：内部完整跑一个独立工作单元，含它自己的阶段发布</summary>
    [UnitOfWorkEventHandler(Phase = UnitOfWorkPhase.BeforeCommit)]
    public sealed class NestingOuterFirstHandler(PhaseRecorder recorder, IUnitOfWorkManager manager)
        : IEventHandler<NestingEvent>
    {
        public async Task HandleAsync(NestingEvent @event, CancellationToken cancellationToken = default)
        {
            var inner = manager.Begin(requiresNew: true);
            inner.AddPendingEvents([new ProbeEvent()]);
            await inner.CompleteAsync(cancellationToken);
            inner.Dispose();

            recorder.NestingObserved.Add(UnitOfWorkContext.CurrentPhase);
        }
    }

    /// <summary>外层第二个处理器：内层跑完之后，它仍须被认作 BeforeCommit</summary>
    [UnitOfWorkEventHandler(Phase = UnitOfWorkPhase.BeforeCommit)]
    public sealed class NestingOuterSecondHandler(PhaseRecorder recorder) : IEventHandler<NestingEvent>
    {
        public Task HandleAsync(NestingEvent @event, CancellationToken cancellationToken = default)
        {
            recorder.NestingObserved.Add(UnitOfWorkContext.CurrentPhase);
            return Task.CompletedTask;
        }
    }
}
