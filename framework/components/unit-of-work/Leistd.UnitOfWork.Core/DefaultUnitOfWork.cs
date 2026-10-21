using Leistd.UnitOfWork.Events;
using Leistd.UnitOfWork.Database;
using Leistd.UnitOfWork.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Leistd.EventBus.Events;
using Leistd.EventBus.Abstractions;

namespace Leistd.UnitOfWork;

/// <inheritdoc/>
public class DefaultUnitOfWork : IUnitOfWork
{
    // 直接分发本工作单元的队列，避免事件经 ILocalEventBus 再次入队。
    // 处理器发布的新事件仍由下一轮排空。
    private readonly ILocalEventDispatcher? _localEventDispatcher;
    private readonly ILogger<DefaultUnitOfWork>? _logger;

    /// <inheritdoc/>
    public Guid Id { get; } = Guid.NewGuid();

    /// <inheritdoc/>
    public IUnitOfWorkOptions Options { get; private set; }

    /// <inheritdoc/>
    public IUnitOfWork? Outer { get; private set; }

    /// <inheritdoc/>
    public bool IsDisposed { get; private set; }

    /// <inheritdoc/>
    public bool IsCompleted { get; private set; }

    private readonly List<ILocalEvent> _pendingEvents = new();

    private readonly List<ILocalEvent> _eventsForLaterPhases = new();

    /// <inheritdoc/>
    public event EventHandler<UnitOfWorkFailedEventArgs>? Failed;

    // 管理器据此同步释放工作单元作用域。
    /// <inheritdoc/>
    public event EventHandler<UnitOfWorkEventArgs>? Disposed;

    /// <inheritdoc/>
    public IServiceProvider ServiceProvider { get; }

    private readonly Dictionary<string, IDatabaseApi> _databaseApis = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ITransactionApi> _transactionApis = new(StringComparer.Ordinal);

    private Exception? _exception;
    private bool _isCompleting;
    private bool _isRolledback;
    private bool _isInitialized;

    /// <summary>使用容器服务和默认选项创建工作单元。</summary>
    public DefaultUnitOfWork(IServiceProvider serviceProvider, IOptions<UnitOfWorkOptions> options)
    {
        ServiceProvider = serviceProvider;
        // 克隆单例快照，避免工作单元共享可变选项。
        Options = options.Value.Clone();
        _localEventDispatcher = serviceProvider.GetService<ILocalEventDispatcher>();
        _logger = serviceProvider.GetService<ILogger<DefaultUnitOfWork>>();

        _logger?.LogDebug("Created unit of work {UowId}", Id);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 只能初始化一次：允许覆盖时，调用方能在事务已按旧值开启之后改掉隔离级别与超时。
    /// 入参会被克隆，工作单元不与调用方共享可变状态。
    /// </remarks>
    public virtual void Initialize(UnitOfWorkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_isInitialized)
        {
            throw new InvalidOperationException(
                $"Unit of work {Id} has already been initialized; its options cannot be replaced.");
        }

        Options = options.Clone();
        _isInitialized = true;
    }

    /// <inheritdoc/>
    public virtual void SetOuter(IUnitOfWork? outer)
    {
        Outer = outer;
    }

    /// <inheritdoc/>
    public virtual async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (_isRolledback)
        {
            return;
        }

        // 完成或释放后保存会落到原事务边界之外。
        if (IsCompleted || IsDisposed)
        {
            throw new InvalidOperationException(
                $"Unit of work {Id} has already completed or been disposed; changes can no longer be flushed into its transaction.");
        }

        // 每次重取列表，以包含 BeforeCommit 处理器新登记的数据库 API。
        foreach (var databaseApi in _databaseApis.Values.OfType<ISupportsSavingChanges>().ToArray())
        {
            await databaseApi.SaveChangesAsync(cancellationToken);
        }
    }

    /// <inheritdoc/>
    public virtual async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_isRolledback)
        {
            return;
        }

        PreventMultipleComplete();

        // 调用方取消只在提交开始之前降级；提交及提交后阶段（AfterCommit 处理器）的任何失败都是真故障
        var commitStarted = false;
        try
        {
            _isCompleting = true;

            // 反复保存并发布 BeforeCommit 事件，直到处理器不再产生新变更。
            // 无数据库的工作单元也必须排空显式加入的事件。
            while (true)
            {
                await SaveChangesAsync(cancellationToken);

                if (_pendingEvents.Count == 0)
                {
                    break;
                }

                // 仅在确有待发事件时要求分发器，避免静默丢弃事件。
                if (_localEventDispatcher is null)
                {
                    throw new InvalidOperationException(
                        $"Unit of work {Id} has {_pendingEvents.Count} pending event(s) but no " +
                        $"{nameof(ILocalEventDispatcher)} is registered, so they cannot be published. " +
                        "Register the local event bus (AddLocalEventBus), or provide an " +
                        $"{nameof(ILocalEventDispatcher)} alongside a custom local event bus.");
                }

                var events = _pendingEvents.ToList();
                _pendingEvents.Clear();

                _logger?.LogDebug("Unit of work {UowId} publishing {Count} BeforeCommit-phase event(s)", Id, events.Count);

                using (UnitOfWorkContext.EnterPhase(UnitOfWorkPhase.BeforeCommit))
                {
                    foreach (var @event in events)
                    {
                        await _localEventDispatcher.DispatchAsync(@event, cancellationToken);
                    }
                }

                _eventsForLaterPhases.AddRange(events);
            }

            // 事务提交前最后一次响应取消；提交开始后不能再报告可重试的取消。
            // 非事务型保存已不可逆，因此跳过这道取消边界。
            if (Options.IsTransactional)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            commitStarted = true;
            await CommitTransactionsAsync();

            // 提交后先置为完成，阻止回滚并让 AfterCommit 处理器脱离已提交的环境工作单元。
            IsCompleted = true;

            _logger?.LogDebug("Unit of work {UowId} committed", Id);

            await OnCompletedAsync();
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested && !commitStarted)
        {
            // 提交开始之前的调用方取消（如客户端断开）不记为失败。事务型什么都没提交；非事务型此前的保存已各自落库，不会回滚。
            _exception = ex;
            if (Options.IsTransactional)
            {
                _logger?.LogDebug("Unit of work {UowId} was cancelled by the caller before commit; nothing was committed", Id);
            }
            else
            {
                _logger?.LogWarning(
                    "Unit of work {UowId} was cancelled by the caller; any changes already saved by this non-transactional unit of work are not rolled back",
                    Id);
            }

            throw;
        }
        catch (Exception ex)
        {
            _exception = ex;
            if (IsCompleted)
            {
                _logger?.LogError(ex, "Unit of work {UowId} committed, but after-commit processing failed", Id);
            }
            else
            {
                _logger?.LogError(ex, "Unit of work {UowId} commit failed", Id);
            }
            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 回滚是幂等的；已提交的工作单元不会回滚。
    /// </remarks>
    public virtual async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (_isRolledback || IsCompleted)
        {
            return;
        }

        _isRolledback = true;
        // Debug 级：每次业务拒绝都会回滚，原因已由异常处理或提交失败日志记录
        _logger?.LogDebug("Unit of work {UowId} rolling back", Id);

        await RollbackAllAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public IDatabaseApi? FindDatabaseApi(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _databaseApis.GetValueOrDefault(key);
    }

    /// <inheritdoc/>
    public void AddDatabaseApi(string key, IDatabaseApi api)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(api);
        if (!_databaseApis.TryAdd(key, api))
        {
            throw new InvalidOperationException($"Database API key '{key}' is already registered in this unit of work.");
        }
    }

    /// <inheritdoc/>
    public ITransactionApi? FindTransactionApi(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _transactionApis.GetValueOrDefault(key);
    }

    /// <inheritdoc/>
    public void AddTransactionApi(string key, ITransactionApi api)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(api);
        if (!_transactionApis.TryAdd(key, api))
        {
            throw new InvalidOperationException($"Transaction API key '{key}' is already registered in this unit of work.");
        }
    }

    /// <inheritdoc/>
    public void AddPendingEvents(IEnumerable<ILocalEvent> events)
    {
        _pendingEvents.AddRange(events);
    }

    /// <summary>发布提交后事件。</summary>
    /// <remarks>所有事件分发完毕后再上抛失败；单个异常保留原类型，多个异常聚合。事务已提交，不再回滚。</remarks>
    protected virtual async Task OnCompletedAsync()
    {
        // 队列非空即意味着分发器存在：事件只能经上面那道守卫之后才进 _eventsForLaterPhases
        if (_eventsForLaterPhases.Count > 0)
        {
            _logger?.LogDebug("Unit of work {UowId} publishing {Count} AfterCommit-phase event(s)", Id, _eventsForLaterPhases.Count);

            // 嵌套工作单元完成后会重新暴露外层，因此提交后阶段必须显式清空环境工作单元。
            var ambientUnitOfWork = ServiceProvider.GetService<IAmbientUnitOfWork>();
            var restored = ambientUnitOfWork?.Get();
            ambientUnitOfWork?.Set(null);
            List<Exception>? failures = null;

            try
            {
                using (UnitOfWorkContext.EnterPhase(UnitOfWorkPhase.AfterCommit))
                {
                    foreach (var @event in _eventsForLaterPhases)
                    {
                        try
                        {
                            await _localEventDispatcher!.DispatchAsync(@event, CancellationToken.None);
                        }
                        catch (Exception exception)
                        {
                            // 已提交的事件相互独立，不能因一次副作用失败而跳过后续安全记录。
                            (failures ??= []).Add(exception);
                        }
                    }
                }
            }
            finally
            {
                ambientUnitOfWork?.Set(restored);
            }

            if (failures is { Count: 1 })
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }

            if (failures is { Count: > 1 })
            {
                throw new AggregateException(
                    $"{failures.Count} AfterCommit event dispatch(es) failed in unit of work '{Id}'.",
                    failures);
            }
        }
    }

    /// <summary>触发释放事件。</summary>
    protected virtual void OnDisposed()
    {
        Disposed?.Invoke(this, new UnitOfWorkEventArgs(this));
    }

    /// <summary>通知失败观察者；观察者抛出的异常不会中断释放。</summary>
    protected virtual void OnFailed()
    {
        var args = new UnitOfWorkFailedEventArgs(this, _exception, _isRolledback);
        foreach (EventHandler<UnitOfWorkFailedEventArgs> handler in Failed?.GetInvocationList() ?? [])
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "Unit of work {UowId} failure observer failed", Id);
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 同步且幂等；未完成或提交失败时触发 <see cref="Failed"/>，不启动后台任务。
    /// </remarks>
    public virtual void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;

        // 工作单元拥有事务，但不拥有数据库 API 指向的作用域服务。
        DisposeTransactions();

        if (!IsCompleted || _exception is not null)
        {
            OnFailed();
        }

        _logger?.LogDebug("Unit of work {UowId} disposed", Id);

        OnDisposed();
    }

    // 各事务独立释放并记录失败，避免一个异常跳过其余事务或覆盖原始异常。
    private void DisposeTransactions()
    {
        foreach (var (key, api) in _transactionApis)
        {
            try
            {
                api.Dispose();
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(
                    exception,
                    "Unit of work {UowId} failed to dispose transaction API '{Key}'",
                    Id, key);
            }
        }
    }

    private void PreventMultipleComplete()
    {
        if (IsCompleted || _isCompleting)
        {
            throw new InvalidOperationException("The unit of work has already completed; CompleteAsync cannot be called twice.");
        }
    }

    /// <summary>回滚所有数据库和事务操作。</summary>
    protected virtual async Task RollbackAllAsync(CancellationToken cancellationToken)
    {
        // 各 API 独立回滚并记录失败，避免一个异常跳过其余 API 或覆盖原始异常。
        foreach (var (key, api) in _databaseApis)
        {
            await RollbackOneAsync(key, api as ISupportsRollback, "database API");
        }

        foreach (var (key, api) in _transactionApis)
        {
            await RollbackOneAsync(key, api as ISupportsRollback, "transaction API");
        }

        async Task RollbackOneAsync(string key, ISupportsRollback? api, string kind)
        {
            if (api is null)
            {
                return;
            }

            try
            {
                await api.RollbackAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(
                    exception,
                    "Unit of work {UowId} failed to roll back {Kind} '{Key}'",
                    Id, kind, key);
            }
        }
    }

    /// <summary>依次提交本工作单元登记的事务。</summary>
    /// <remarks>
    /// 不提供跨事务原子性；后续提交失败时抛出包含已提交项的 <see cref="InvalidOperationException"/>。
    /// </remarks>
    protected virtual async Task CommitTransactionsAsync()
    {
        var committed = new List<string>();

        foreach (var (key, transactionApi) in _transactionApis)
        {
            try
            {
                await transactionApi.CommitAsync();
            }
            catch (Exception exception) when (committed.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The unit of work partially committed: {committed.Count} transaction(s) " +
                    $"[{string.Join(", ", committed)}] were already committed when '{key}' failed. " +
                    "Those commits cannot be rolled back; the data requires manual reconciliation.",
                    exception);
            }

            committed.Add(key);
        }
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"[UnitOfWork {Id}]";
    }
}
