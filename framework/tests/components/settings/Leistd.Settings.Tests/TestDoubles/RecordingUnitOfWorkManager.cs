using Leistd.EventBus.Events;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.Database;
using Leistd.UnitOfWork.Events;
using Leistd.UnitOfWork.Options;

namespace Leistd.Settings.Tests.TestDoubles;

// 不连库的工作单元管理器：记下每次开启的选项与提交、回滚、释放的先后，供断言写入入口的事务边界
internal sealed class RecordingUnitOfWorkManager(Action<string>? journal = null) : IUnitOfWorkManager
{
    private readonly List<RecordingUnitOfWork> _begun = [];

    public IReadOnlyList<RecordingUnitOfWork> Begun
    {
        get
        {
            lock (_begun)
            {
                return [.. _begun];
            }
        }
    }

    public IUnitOfWork? Current => null;

    public IUnitOfWork Begin(UnitOfWorkOptions? options = null, bool requiresNew = false)
    {
        var unitOfWork = new RecordingUnitOfWork(options?.Clone() ?? new UnitOfWorkOptions(), requiresNew, journal);
        lock (_begun)
        {
            _begun.Add(unitOfWork);
        }

        journal?.Invoke($"begin(requiresNew={requiresNew}, transactional={unitOfWork.Options.IsTransactional})");
        return unitOfWork;
    }
}

internal sealed class RecordingUnitOfWork(UnitOfWorkOptions options, bool requiresNew, Action<string>? journal) : IUnitOfWork
{
    public event EventHandler<UnitOfWorkFailedEventArgs>? Failed;

    public event EventHandler<UnitOfWorkEventArgs>? Disposed;

    public Guid Id { get; } = Guid.NewGuid();

    public IUnitOfWorkOptions Options { get; private set; } = options;

    public bool RequiresNew { get; } = requiresNew;

    public IUnitOfWork? Outer { get; private set; }

    public IServiceProvider ServiceProvider => throw new NotSupportedException("The recording unit of work has no service scope.");

    public bool IsDisposed { get; private set; }

    public bool IsCompleted { get; private set; }

    public bool IsRolledBack { get; private set; }

    public void SetOuter(IUnitOfWork? outer) => Outer = outer;

    public void Initialize(UnitOfWorkOptions options) => Options = options.Clone();

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        // 与事务型默认实现同一边界：提交开始前最后一次响应取消
        cancellationToken.ThrowIfCancellationRequested();
        IsCompleted = true;
        journal?.Invoke("commit");
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (!IsCompleted && !IsRolledBack)
        {
            IsRolledBack = true;
            journal?.Invoke("rollback");
        }

        return Task.CompletedTask;
    }

    public void AddPendingEvents(IEnumerable<ILocalEvent> events) => throw new NotSupportedException();

    public IDatabaseApi? FindDatabaseApi(string key) => null;

    public void AddDatabaseApi(string key, IDatabaseApi api) => throw new NotSupportedException();

    public ITransactionApi? FindTransactionApi(string key) => null;

    public void AddTransactionApi(string key, ITransactionApi api) => throw new NotSupportedException();

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        if (!IsCompleted)
        {
            Failed?.Invoke(this, new UnitOfWorkFailedEventArgs(this, null, IsRolledBack));
        }

        journal?.Invoke("dispose");
        Disposed?.Invoke(this, new UnitOfWorkEventArgs(this));
    }
}
