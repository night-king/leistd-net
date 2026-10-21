using Leistd.UnitOfWork.Events;
using Leistd.UnitOfWork.Database;
using Leistd.UnitOfWork.Options;
using Leistd.EventBus.Events;

namespace Leistd.UnitOfWork;

// 嵌套边界并入外层时返回的子工作单元，全部委托给父级
internal class ChildUnitOfWork : IUnitOfWork
{
    private readonly IUnitOfWork _parent;

    public Guid Id => _parent.Id;
    public IUnitOfWorkOptions Options => _parent.Options;
    public IUnitOfWork? Outer => _parent.Outer;
    public IServiceProvider ServiceProvider => _parent.ServiceProvider;
    public bool IsDisposed => _parent.IsDisposed;
    public bool IsCompleted => _parent.IsCompleted;

    public event EventHandler<UnitOfWorkFailedEventArgs>? Failed
    {
        add => _parent.Failed += value;
        remove => _parent.Failed -= value;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 同样委托给父级：子工作单元的 <see cref="Dispose"/> 是空操作（生命周期归父级），
    /// 因此"释放"这件事只会由父级发出一次。管理器也只为父级那次边界建了作用域。
    /// </remarks>
    public event EventHandler<UnitOfWorkEventArgs>? Disposed
    {
        add => _parent.Disposed += value;
        remove => _parent.Disposed -= value;
    }

    /// <remarks>
    /// 子工作单元没有独立生命周期；<see cref="Failed"/> 与 <see cref="Disposed"/> 的
    /// add/remove 直接委托给父级，不创建额外的中转委托。
    /// </remarks>
    public ChildUnitOfWork(IUnitOfWork parent)
    {
        _parent = parent;
    }

    public void SetOuter(IUnitOfWork? outer) => _parent.SetOuter(outer);

    public void Initialize(UnitOfWorkOptions options) => _parent.Initialize(options);

    // 转发给父级：子工作单元没有自己的数据库 API，全部登记在父级上
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => _parent.SaveChangesAsync(cancellationToken);

    public Task CompleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RollbackAsync(CancellationToken cancellationToken = default) => _parent.RollbackAsync(cancellationToken);

    public void AddPendingEvents(IEnumerable<ILocalEvent> events) => _parent.AddPendingEvents(events);

    public IDatabaseApi? FindDatabaseApi(string key) => _parent.FindDatabaseApi(key);

    public void AddDatabaseApi(string key, IDatabaseApi api) => _parent.AddDatabaseApi(key, api);

    public ITransactionApi? FindTransactionApi(string key) => _parent.FindTransactionApi(key);

    public void AddTransactionApi(string key, ITransactionApi api) => _parent.AddTransactionApi(key, api);

    public void Dispose() { }

    /// <inheritdoc />
    public override string ToString() => $"[ChildUnitOfWork {Id}]";
}
