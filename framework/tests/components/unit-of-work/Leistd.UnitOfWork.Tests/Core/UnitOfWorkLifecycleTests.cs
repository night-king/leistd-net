using Leistd.DependencyInjection.DynamicProxy.Registration;
using Leistd.EventBus.EventHandlers;
using Leistd.EventBus.Events;
using Leistd.EventBus.Local;
using Leistd.UnitOfWork.Attributes;
using Leistd.UnitOfWork.Database;
using Leistd.UnitOfWork.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.UnitOfWork.Tests.Core;

/// <summary>工作单元的终结生命周期：作用域释放、阶段调度与 exactly-once。</summary>
/// <remarks>
/// <para>这一组钉的是<b>拦截器路径</b>。显式 <c>using (await Begin())</c> 的调用点由
/// <c>using</c> 保证释放，而 <c>[UnitOfWork]</c> 特性的调用点完全依赖拦截器——
/// 成功路径若只调 <c>CompleteAsync()</c>、不 <c>Dispose()</c>，
/// 管理器为每个工作单元创建的 DI 作用域在正常路径上永不释放。</para>
/// <para>作用域是否释放通过一个注册为 Scoped 的探针观察：<c>IDisposable.Dispose</c> 被调用
/// 即说明它所在的作用域被释放了。这比断言内部字段可靠——它测的是可观察后果。</para>
/// </remarks>
public sealed class UnitOfWorkLifecycleTests
{
    [Fact]
    public async Task Failed_event_reports_commit_exception_when_unit_of_work_is_disposed()
    {
        var services = BaseServices(new ScopeProbeRegistry());
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        var uow = manager.Begin();
        var failure = new InvalidOperationException("commit failed");
        uow.AddTransactionApi("failing", new FailingCommitTransactionApi(failure));
        UnitOfWorkFailedEventArgs? reported = null;
        uow.Failed += (_, args) => reported = args;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => uow.CompleteAsync());
        uow.Dispose();

        Assert.Same(failure, thrown);
        Assert.NotNull(reported);
        Assert.Same(uow, reported.UnitOfWork);
        Assert.Same(failure, reported.Exception);
        Assert.False(reported.IsRolledBack);
    }

    [Fact]
    public async Task Failed_event_handler_exception_does_not_escape_dispose()
    {
        var services = BaseServices(new ScopeProbeRegistry());
        await using var provider = services.BuildServiceProvider();
        var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin();
        var secondHandlerRan = false;
        uow.Failed += (_, _) => throw new InvalidOperationException("observer failed");
        uow.Failed += (_, _) => secondHandlerRan = true;

        var exception = Record.Exception(uow.Dispose);

        Assert.Null(exception);
        Assert.True(secondHandlerRan);
    }

    [Fact]
    public async Task Child_subscription_observes_parent_failure()
    {
        var services = BaseServices(new ScopeProbeRegistry());
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        var parent = manager.Begin();
        var child = manager.Begin(requiresNew: false);
        UnitOfWorkFailedEventArgs? reported = null;
        child.Failed += (_, args) => reported = args;

        parent.Dispose();

        Assert.NotNull(reported);
        Assert.Same(parent, reported.UnitOfWork);
    }

    [Fact]
    public async Task Successful_interception_releases_the_unit_of_work_scope()
    {
        var probes = new ScopeProbeRegistry();
        await using var provider = Build(probes);

        await provider.GetRequiredService<IProbedService>().SucceedAsync();

        Assert.Single(probes.Created);
        Assert.All(probes.Created, probe => Assert.True(probe.IsDisposed));
    }

    [Fact]
    public async Task Failing_interception_releases_the_unit_of_work_scope()
    {
        var probes = new ScopeProbeRegistry();
        await using var provider = Build(probes);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRequiredService<IProbedService>().FailAsync());

        Assert.Single(probes.Created);
        Assert.All(probes.Created, probe => Assert.True(probe.IsDisposed));
    }

    /// <summary>返回 <c>Task&lt;T&gt;</c> 的成功调用同样释放作用域</summary>
    /// <remarks>
    /// 拦截器的泛型与非泛型重载是两段独立代码，而<b>应用服务的常态形态恰恰是泛型</b>
    /// （任何返回 DTO 的方法都走这里）。只用 <c>Task</c> 方法的用例覆盖不到泛型那一支，
    /// "成功路径泄漏作用域"在真实调用里留着，测试却全绿。
    /// 这两条用例让两个重载不能分头演化。
    /// </remarks>
    [Fact]
    public async Task Successful_generic_interception_releases_the_unit_of_work_scope()
    {
        var probes = new ScopeProbeRegistry();
        await using var provider = Build(probes);

        var result = await provider.GetRequiredService<IProbedService>().SucceedWithResultAsync();

        Assert.Equal(42, result);
        Assert.Single(probes.Created);
        Assert.All(probes.Created, probe => Assert.True(probe.IsDisposed));
    }

    /// <summary>返回 <c>Task&lt;T&gt;</c> 的失败调用回滚并释放作用域</summary>
    [Fact]
    public async Task Failing_generic_interception_rolls_back_and_releases_the_scope()
    {
        var probes = new ScopeProbeRegistry();
        await using var provider = Build(probes);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRequiredService<IProbedService>().FailWithResultAsync());

        var probe = Assert.Single(probes.Created);
        Assert.True(probe.IsDisposed);

        // 只断言作用域释放不够：Dispose 自己也会释放作用域，
        // 因此缺了 RollbackAsync 的 catch 分支照样能通过。这条断言才钉住回滚真的发生过
        Assert.True(probe.Transaction!.RolledBack);
        Assert.False(probe.Transaction.Committed);
    }

    /// <summary>成功路径提交事务，且不回滚</summary>
    [Fact]
    public async Task Successful_generic_interception_commits_the_transaction()
    {
        var probes = new ScopeProbeRegistry();
        await using var provider = Build(probes);

        await provider.GetRequiredService<IProbedService>().SucceedWithResultAsync();

        var probe = Assert.Single(probes.Created);
        Assert.True(probe.Transaction!.Committed);
        Assert.False(probe.Transaction.RolledBack);
    }

    /// <summary>AfterCommit 处理器在作用域仍存活时被 await 完成。</summary>
    /// <remarks>
    /// 处理器解析的是同一作用域里的 Scoped 探针。若阶段事件被 fire-and-forget，
    /// 这里要么拿不到调用记录、要么在探针已释放后才跑到。
    /// </remarks>
    [Fact]
    public async Task After_commit_handlers_run_before_the_scope_is_released()
    {
        var probes = new ScopeProbeRegistry();
        await using var provider = Build(probes);

        await provider.GetRequiredService<IProbedService>().RaiseAsync();

        // 处理器跑过，且跑的时候它自己所在的作用域还活着——这正是 fire-and-forget 做不到的
        var handled = Assert.Single(probes.Created, probe => probe.HandlerRan);
        Assert.False(handled.WasDisposedBeforeHandler);

        // 调用返回后所有作用域都已释放
        Assert.All(probes.Created, probe => Assert.True(probe.IsDisposed));
    }

    /// <summary>BeforeCommit 阶段新登记的数据库 API 也要被保存</summary>
    /// <remarks>
    /// <para>处理器解析一个此前没碰过的仓储时，<c>DbContextProvider</c> 会在<b>循环进行中</b>
    /// 向本工作单元登记新的数据库 API。若保存阶段沿用循环开始时的快照，这个 DbContext 永远
    /// 不会 <c>SaveChanges</c>，而事务照常提交——变更静默丢失，调用方看到的是"提交成功"。</para>
    /// <para>用假件复现整条时序：处理器在 BeforeCommit 里登记第二个数据库 API，
    /// 断言它<b>在事务提交之前</b>被保存过。只断言"最终被保存过"不够——那样即使保存跑到
    /// 提交之后也能通过，而提交之后再保存等于没进这次事务。</para>
    /// </remarks>
    [Fact]
    public async Task Database_api_registered_during_before_commit_is_saved_before_commit()
    {
        var probes = new ScopeProbeRegistry();
        await using var provider = Build(probes);

        await provider.GetRequiredService<IProbedService>().RaiseLateDatabaseAsync();

        // 处理器自身也解析 Scoped 探针，因此作用域探针不止一个；取登记了数据库假件的那一个
        var late = Assert.Single(probes.Created.Select(probe => probe.LateDatabase).OfType<ProbeDatabaseApi>());
        Assert.True(late.SaveChangesCalled);

        var entries = provider.GetRequiredService<CallLog>().Entries.ToList();
        Assert.Contains("save:late", entries);
        Assert.Contains("commit", entries);
        Assert.True(
            entries.IndexOf("save:late") < entries.IndexOf("commit"),
            $"Saving must happen before committing, actual order: {string.Join(" -> ", entries)}");
    }

    /// <summary>回滚与释放各自最多发生一次</summary>
    [Fact]
    public async Task Rollback_and_dispose_happen_at_most_once()
    {
        var services = BaseServices(new ScopeProbeRegistry());
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        var uow = manager.Begin();
        await uow.RollbackAsync();
        await uow.RollbackAsync();
        uow.Dispose();
        uow.Dispose();

        Assert.True(uow.IsDisposed);
    }

    /// <summary>已提交的工作单元不再回滚：事务已经提交，"回滚"无从执行</summary>
    [Fact]
    public async Task Completed_unit_of_work_is_not_rolled_back()
    {
        var services = BaseServices(new ScopeProbeRegistry());
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        using var uow = manager.Begin();
        await uow.CompleteAsync();
        await uow.RollbackAsync();

        Assert.True(uow.IsCompleted);
    }

    private static ServiceCollection BaseServices(ScopeProbeRegistry probes)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalEventBus();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUnitOfWork();
        services.AddSingleton(probes);
        services.AddSingleton<CallLog>();
        services.AddScoped<ScopeProbe>();
        services.AddTransient<IProbedService, ProbedService>();
        services.AddTransient<IEventHandler<ProbeEvent>, ProbeEventHandler>();
        services.AddTransient<IEventHandler<LateDatabaseEvent>, LateDatabaseEventHandler>();
        return services;
    }

    private static ServiceProvider Build(ScopeProbeRegistry probes) =>
        (ServiceProvider)new DynamicProxyServiceRegistrationCallbackFactory()
            .CreateServiceProvider(BaseServices(probes));

    public sealed class ScopeProbeRegistry
    {
        public List<ScopeProbe> Created { get; } = [];
    }

    private sealed class FailingCommitTransactionApi(Exception exception) : ITransactionApi
    {
        public Task CommitAsync() => Task.FromException(exception);

        public void Dispose() { }
    }

    /// <summary>Scoped 探针：被释放即说明它所在的作用域被释放了</summary>
    public sealed class ScopeProbe : IDisposable
    {
        public ScopeProbe(ScopeProbeRegistry registry) => registry.Created.Add(this);

        public bool IsDisposed { get; private set; }

        public bool HandlerRan { get; private set; }

        public bool WasDisposedBeforeHandler { get; private set; }

        /// <summary>本作用域登记进工作单元的事务假件（未登记时为 <see langword="null"/>）</summary>
        public ProbeTransactionApi? Transaction { get; set; }

        /// <summary>BeforeCommit 处理器登记的数据库假件</summary>
        public ProbeDatabaseApi? LateDatabase { get; set; }

        public void MarkHandlerRan()
        {
            WasDisposedBeforeHandler = IsDisposed;
            HandlerRan = true;
        }

        public void Dispose() => IsDisposed = true;
    }

    /// <summary>假件共享的调用顺序记录：断言"保存发生在提交之前"需要的是次序，不是布尔量</summary>
    public sealed class CallLog
    {
        private readonly List<string> _entries = [];

        public IReadOnlyList<string> Entries => _entries;

        public void Record(string entry) => _entries.Add(entry);
    }

    /// <summary>数据库假件：记录 SaveChanges 的发生与次序</summary>
    public sealed class ProbeDatabaseApi(CallLog log, string name) : IDatabaseApi, ISupportsSavingChanges
    {
        public bool SaveChangesCalled { get; private set; }

        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCalled = true;
            log.Record($"save:{name}");
            return Task.CompletedTask;
        }
    }

    /// <summary>事务假件：记录提交/回滚的发生与次序</summary>
    public sealed class ProbeTransactionApi(CallLog log) : ITransactionApi, ISupportsRollback
    {
        public bool Committed { get; private set; }

        public bool RolledBack { get; private set; }

        public Task CommitAsync()
        {
            Committed = true;
            log.Record("commit");
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RolledBack = true;
            log.Record("rollback");
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    public sealed class ProbeEvent : LocalEvent;

    [UnitOfWorkEventHandler(Phase = UnitOfWorkPhase.AfterCommit)]
    public sealed class ProbeEventHandler(ScopeProbe probe) : IEventHandler<ProbeEvent>
    {
        public Task HandleAsync(ProbeEvent @event, CancellationToken cancellationToken = default)
        {
            probe.MarkHandlerRan();
            return Task.CompletedTask;
        }
    }

    public sealed class LateDatabaseEvent : LocalEvent;

    /// <summary>在 BeforeCommit 阶段登记一个新的数据库 API——与 DbContextProvider 的时机一致</summary>
    [UnitOfWorkEventHandler(Phase = UnitOfWorkPhase.BeforeCommit)]
    public sealed class LateDatabaseEventHandler(
        ScopeProbe probe, IUnitOfWorkManager unitOfWorkManager, CallLog log)
        : IEventHandler<LateDatabaseEvent>
    {
        public Task HandleAsync(LateDatabaseEvent @event, CancellationToken cancellationToken = default)
        {
            var late = new ProbeDatabaseApi(log, "late");
            probe.LateDatabase = late;
            unitOfWorkManager.Current!.AddDatabaseApi("late", late);
            return Task.CompletedTask;
        }
    }

    public interface IProbedService
    {
        Task SucceedAsync();

        Task FailAsync();

        Task RaiseAsync();

        Task RaiseLateDatabaseAsync();

        Task<int> SucceedWithResultAsync();

        Task<int> FailWithResultAsync();
    }

    public sealed class ProbedService(IUnitOfWorkManager unitOfWorkManager, CallLog log) : IProbedService
    {
        [UnitOfWork]
        public Task SucceedAsync()
        {
            // 触碰作用域内的探针，确保作用域真的被创建过
            _ = Current();
            return Task.CompletedTask;
        }

        [UnitOfWork]
        public Task FailAsync()
        {
            _ = Current();
            throw new InvalidOperationException("boom");
        }

        [UnitOfWork]
        public Task RaiseAsync()
        {
            var uow = unitOfWorkManager.Current!;
            _ = Current();
            uow.AddPendingEvents([new ProbeEvent()]);
            return Task.CompletedTask;
        }

        [UnitOfWork]
        public Task RaiseLateDatabaseAsync()
        {
            var uow = unitOfWorkManager.Current!;
            RegisterTransaction();
            uow.AddPendingEvents([new LateDatabaseEvent()]);
            return Task.CompletedTask;
        }

        [UnitOfWork]
        public Task<int> SucceedWithResultAsync()
        {
            RegisterTransaction();
            return Task.FromResult(42);
        }

        [UnitOfWork]
        public Task<int> FailWithResultAsync()
        {
            RegisterTransaction();
            throw new InvalidOperationException("boom");
        }

        /// <summary>给当前工作单元登记一个事务假件，并挂到本作用域的探针上供断言读取</summary>
        private void RegisterTransaction()
        {
            var transaction = new ProbeTransactionApi(log);
            Current().Transaction = transaction;
            unitOfWorkManager.Current!.AddTransactionApi("probe", transaction);
        }

        private ScopeProbe Current() =>
            ((DefaultUnitOfWork)unitOfWorkManager.Current!)
                .ServiceProvider.GetRequiredService<ScopeProbe>();
    }
}
