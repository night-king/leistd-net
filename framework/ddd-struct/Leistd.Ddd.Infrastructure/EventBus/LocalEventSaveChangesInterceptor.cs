using System.Runtime.CompilerServices;
using Leistd.Ddd.Domain.Entities;
using Leistd.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Leistd.EventBus.Events;
using Leistd.EventBus.Abstractions;

namespace Leistd.Ddd.Infrastructure.EventBus;

/// <summary>在 EF Core 保存周期中收集并发布本地事件。</summary>
/// <remarks>
/// 收集在 <c>SavingChanges</c>（保存前）、发布在 <c>SavedChanges</c>（保存成功后）；保存失败时丢弃已收集的事件。
/// 事件按 DbContext 实例暂存，发布后清理。
/// </remarks>
public class LocalEventSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly ILocalEventBus _localEventBus;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly ILogger<LocalEventSaveChangesInterceptor> _logger;

    // 弱引用按 DbContext 隔离待发布事件，避免延长上下文生命周期。
    private static readonly ConditionalWeakTable<DbContext, List<ILocalEvent>> _pendingByContext = new();

    /// <summary>创建拦截器；依赖由 <c>AddDddInfrastructure</c> 注册。</summary>
    public LocalEventSaveChangesInterceptor(
        ILocalEventBus localEventBus,
        IUnitOfWorkManager unitOfWorkManager,
        ILogger<LocalEventSaveChangesInterceptor> logger)
    {
        _localEventBus = localEventBus;
        _unitOfWorkManager = unitOfWorkManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        CollectInto(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        CollectInto(eventData.Context);
        return new ValueTask<InterceptionResult<int>>(result);
    }

    /// <inheritdoc />
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        PublishCollected(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await PublishCollectedAsync(eventData.Context, cancellationToken);
        return result;
    }

    /// <inheritdoc />
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
        => Discard(eventData.Context);

    /// <inheritdoc />
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context);
        return Task.CompletedTask;
    }

    private void CollectInto(DbContext? context)
    {
        if (context == null)
            return;

        var events = CollectLocalEvents(context);
        ClearLocalEvents(context);

        if (events.Count == 0)
            return;

        // 同一保存周期可能多次进入，必须累加事件。
        var list = _pendingByContext.GetOrCreateValue(context);
        list.AddRange(events);
    }

    private void Discard(DbContext? context)
    {
        if (context != null)
            _pendingByContext.Remove(context);
    }

    private List<ILocalEvent> TakePending(DbContext context)
    {
        if (_pendingByContext.TryGetValue(context, out var list))
        {
            _pendingByContext.Remove(context);
            return list;
        }
        return [];
    }

    private void PublishCollected(DbContext? context)
    {
        if (context == null)
            return;

        var localEvents = TakePending(context);
        if (localEvents.Count == 0)
            return;

        var currentUow = _unitOfWorkManager.Current;

        if (currentUow != null)
        {
            currentUow.AddPendingEvents(localEvents);
            _logger.LogDebug("Collected {Count} event(s); queued in the unit of work for publication", localEvents.Count);
        }
        else
        {
            _logger.LogWarning("Publishing {Count} local event(s) inside synchronous SaveChanges. This may cause thread starvation (sync-over-async). Prefer SaveChangesAsync.", localEvents.Count);

            foreach (var @event in localEvents)
            {
                _localEventBus.PublishAsync(@event).GetAwaiter().GetResult();
            }
        }
    }

    private async Task PublishCollectedAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context == null)
            return;

        var localEvents = TakePending(context);
        if (localEvents.Count == 0)
            return;

        var currentUow = _unitOfWorkManager.Current;

        if (currentUow != null)
        {
            currentUow.AddPendingEvents(localEvents);
            _logger.LogDebug("Collected {Count} event(s); queued in the unit of work for publication", localEvents.Count);
        }
        else
        {
            _logger.LogDebug("No unit of work; publishing {Count} event(s) immediately (default AfterCommit phase)", localEvents.Count);
            foreach (var @event in localEvents)
            {
                await _localEventBus.PublishAsync(@event, cancellationToken);
            }
        }
    }

    // 与清空同一范围：只改了子实体或只登记事件的聚合根保存时是 Unchanged，按状态过滤会清掉却不发布
    private static List<ILocalEvent> CollectLocalEvents(DbContext context)
    {
        return context.ChangeTracker
            .Entries<Entity>()
            .SelectMany(e => e.Entity.GetLocalEvents())
            .ToList();
    }

    private static void ClearLocalEvents(DbContext context)
    {
        context.ChangeTracker
            .Entries<Entity>()
            .ToList()
            .ForEach(e => e.Entity.ClearLocalEvents());
    }
}
