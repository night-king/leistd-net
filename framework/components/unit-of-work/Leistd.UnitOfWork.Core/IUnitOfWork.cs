using Leistd.UnitOfWork.Database;
using Leistd.UnitOfWork.Events;
using Leistd.UnitOfWork.Options;
using Leistd.EventBus.Events;

namespace Leistd.UnitOfWork;

/// <summary>协调数据库保存、本地事件阶段和事务边界。</summary>
public interface IUnitOfWork : IDatabaseApiContainer, ITransactionApiContainer, IDisposable
{
    /// <summary>未完成即释放工作单元时同步触发。</summary>
    event EventHandler<UnitOfWorkFailedEventArgs>? Failed;

    /// <summary>工作单元释放时同步触发，无论是否已完成。</summary>
    /// <remarks>
    /// 自定义实现必须在释放时发出一次本事件，供管理器回收 DI 作用域并恢复外层工作单元。
    /// <see cref="IDisposable.Dispose"/> 必须幂等。
    /// </remarks>
    event EventHandler<UnitOfWorkEventArgs>? Disposed;

    /// <summary>工作单元的唯一标识。</summary>
    Guid Id { get; }

    /// <summary>本工作单元的有效选项。</summary>
    IUnitOfWorkOptions Options { get; }

    /// <summary>外层工作单元；顶层边界为 <see langword="null"/>。</summary>
    IUnitOfWork? Outer { get; }

    /// <summary>本工作单元的服务作用域。</summary>
    /// <remarks>
    /// 自定义实现必须提供当前边界的 DI 作用域，供数据库提供方解析上下文与连接。
    /// 子工作单元复用父级作用域、数据库与事务。
    /// </remarks>
    IServiceProvider ServiceProvider { get; }

    /// <summary>是否已释放。</summary>
    bool IsDisposed { get; }

    /// <summary>是否已完成（已提交）。</summary>
    bool IsCompleted { get; }

    /// <summary>设置嵌套边界的外层工作单元。</summary>
    void SetOuter(IUnitOfWork? outer);

    /// <summary>使用本次边界的选项初始化工作单元。</summary>
    void Initialize(UnitOfWorkOptions options);

    /// <summary>把已登记数据库 API 的挂起变更推送到数据库，不提交事务。</summary>
    /// <remarks>
    /// 事务型冲刷仍可回滚；非事务型每次冲刷独立持久化。
    /// 需要数据库生成值时可提前调用。已回滚时为空操作；
    /// 已完成或已释放时抛 <see cref="InvalidOperationException"/>。
    /// </remarks>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>保存变更，发布提交前事件，提交事务，再发布提交后事件。</summary>
    /// <remarks>
    /// 事务型：<c>SaveChanges</c> 与 BeforeCommit 处理器响应 <paramref name="cancellationToken"/>，
    /// 提交前做最后一次取消检查，此时抛 <see cref="OperationCanceledException"/> 且什么都没提交；越过该边界后不再响应取消。
    /// 非事务型：没有提交阶段，不承诺“什么都没提交”，也不在末尾追加取消检查。
    /// </remarks>
    Task CompleteAsync(CancellationToken cancellationToken = default);

    /// <summary>回滚本工作单元已开启的事务。</summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);

    /// <summary>登记由基础设施在工作单元阶段中发布的本地事件。</summary>
    void AddPendingEvents(IEnumerable<ILocalEvent> events);
}
