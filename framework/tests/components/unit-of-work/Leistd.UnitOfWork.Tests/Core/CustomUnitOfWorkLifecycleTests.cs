using Leistd.EventBus.Events;
using Leistd.UnitOfWork.Database;
using Leistd.UnitOfWork.Options;
using Leistd.UnitOfWork.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.UnitOfWork.Tests.Core;

/// <summary>自定义工作单元的作用域回收与环境恢复。</summary>
/// <remarks>
/// 宿主可先注册实现覆盖 <c>TryAdd</c> 默认项；管理器须依赖 <c>IUnitOfWork.Disposed</c> 契约回收独立作用域，不能按具体实现类型订阅。
/// </remarks>
public sealed class CustomUnitOfWorkLifecycleTests
{
    [Fact]
    public async Task A_custom_implementation_gets_its_scope_released_on_dispose()
    {
        var probes = new ProbeRegistry();
        await using var provider = Build(probes);

        var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin();
        Assert.Single(probes.Created);
        Assert.False(probes.Created[0].IsDisposed);

        uow.Dispose();

        Assert.True(probes.Created[0].IsDisposed);
    }

    [Fact]
    public async Task A_custom_implementation_restores_the_outer_ambient_on_dispose()
    {
        var probes = new ProbeRegistry();
        await using var provider = Build(probes);
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        var outer = manager.Begin();
        var inner = manager.Begin(requiresNew: true);
        Assert.Same(inner, manager.Current);

        inner.Dispose();

        Assert.Same(outer, manager.Current);
    }

    // 契约要求 Dispose() 幂等且释放通知只发一次；重复释放不得把同一个作用域回收两遍。
    [Fact]
    public async Task Disposing_twice_releases_the_scope_once()
    {
        var probes = new ProbeRegistry();
        await using var provider = Build(probes);

        var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin();
        uow.Dispose();
        uow.Dispose();

        Assert.Equal(1, probes.Created[0].DisposeCount);
    }

    // 初始化抛错时调用方还没拿到可释放的句柄：作用域必须当场回收，外层 ambient 一次都不能被覆盖。
    // 若 Initialize() 在 CreateNewUnitOfWork() 返回之后才调用，抛错后作用域没人释放，
    // "当前工作单元"也会停在一个初始化失败的实例上。
    [Fact]
    public async Task A_failed_initialization_releases_the_scope_and_leaves_the_ambient_untouched()
    {
        var probes = new ProbeRegistry();
        var failure = new InitializationFailureSwitch();
        await using var provider = Build(probes, failure);
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        var outer = manager.Begin();
        failure.FailNext = true;

        Assert.Throws<InvalidOperationException>(() => manager.Begin(requiresNew: true));

        // 外层仍是当前工作单元，且失败那次的作用域已经回收
        Assert.Same(outer, manager.Current);
        Assert.Equal(2, probes.Created.Count);
        Assert.True(probes.Created[1].IsDisposed);
        Assert.False(probes.Created[0].IsDisposed);
    }

    private static ServiceProvider Build(ProbeRegistry probes, InitializationFailureSwitch? failure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(failure ?? new InitializationFailureSwitch());
        // 先注册自定义实现：AddUnitOfWork 内部是 TryAdd，因此默认实现不会覆盖它
        services.AddTransient<IUnitOfWork, MinimalUnitOfWork>();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUnitOfWork();
        services.AddSingleton(probes);
        services.AddScoped<ScopeProbe>();
        return services.BuildServiceProvider();
    }

    private sealed class ProbeRegistry
    {
        public List<ScopeProbe> Created { get; } = [];
    }

    /// <summary>让下一次初始化抛错；经容器注入而不是静态字段，避免用例之间互相影响。</summary>
    private sealed class InitializationFailureSwitch
    {
        public bool FailNext { get; set; }
    }

    /// <summary>Scoped 探针：被释放即说明它所在的作用域被释放了。</summary>
    private sealed class ScopeProbe : IDisposable
    {
        public ScopeProbe(ProbeRegistry registry) => registry.Created.Add(this);

        public int DisposeCount { get; private set; }

        public bool IsDisposed => DisposeCount > 0;

        public void Dispose() => DisposeCount++;
    }

    /// <summary>
    /// 最小自定义实现：只做生命周期契约要求的事——解析一次作用域内的服务（让探针被建出来）、
    /// Dispose 幂等、释放时发出一次 <see cref="Disposed"/>。
    /// </summary>
    private sealed class MinimalUnitOfWork : IUnitOfWork
    {
        private readonly InitializationFailureSwitch _failure;

        public MinimalUnitOfWork(IServiceProvider serviceProvider)
        {
            ServiceProvider = serviceProvider;
            _failure = serviceProvider.GetRequiredService<InitializationFailureSwitch>();
            // 让本作用域真的持有一个 scoped 服务，否则"作用域是否释放"无从观察
            serviceProvider.GetRequiredService<ScopeProbe>();
        }

        public event EventHandler<UnitOfWorkFailedEventArgs>? Failed;

        public event EventHandler<UnitOfWorkEventArgs>? Disposed;

        public Guid Id { get; } = Guid.CreateVersion7();

        public IUnitOfWorkOptions Options { get; private set; } = new UnitOfWorkOptions();

        public IUnitOfWork? Outer { get; private set; }

        public IServiceProvider ServiceProvider { get; }

        public bool IsDisposed { get; private set; }

        public bool IsCompleted { get; private set; }

        public void Initialize(UnitOfWorkOptions options)
        {
            if (_failure.FailNext)
            {
                _failure.FailNext = false;
                throw new InvalidOperationException("custom initialization failed");
            }

            Options = options;
        }

        public void SetOuter(IUnitOfWork? outer) => Outer = outer;

        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            IsCompleted = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void AddPendingEvents(IEnumerable<ILocalEvent> events) { }

        public IDatabaseApi? FindDatabaseApi(string key) => null;

        public void AddDatabaseApi(string key, IDatabaseApi api) { }

        public IDatabaseApi GetOrAddDatabaseApi(string key, Func<IDatabaseApi> factory) => factory();

        public ITransactionApi? FindTransactionApi(string key) => null;

        public void AddTransactionApi(string key, ITransactionApi api) { }

        public ITransactionApi GetOrAddTransactionApi(string key, Func<ITransactionApi> factory) => factory();

        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            Failed?.Invoke(this, new UnitOfWorkFailedEventArgs(this, null, false));
            Disposed?.Invoke(this, new UnitOfWorkEventArgs(this));
        }
    }
}
