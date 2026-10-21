using Leistd.Data.Paging;
using Microsoft.EntityFrameworkCore;
using Leistd.Timing;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Leistd.Notifications.Dtos;
using Leistd.Notifications.EntityFrameworkCore.Entities;
using Leistd.Notifications.Stores;

namespace Leistd.Notifications.EntityFrameworkCore.Stores;

/// <summary>使用 EF Core 持久化用户通知和已读状态。</summary>
/// <remarks>
/// 通过 <see cref="IDbContextProvider{TDbContext}"/> 获取当前边界的上下文与连接，参与工作单元。
/// </remarks>
/// <typeparam name="TDbContext">宿主 DbContext 类型（需包含 NotificationRecord 配置）。</typeparam>
public class EfCoreNotificationStore<TDbContext>(
    IDbContextProvider<TDbContext> dbContextProvider,
    IClock clock) : INotificationStore
    where TDbContext : DbContext
{
    /// <inheritdoc/>
    public async Task SaveAsync(NotificationOutputDto notification, string userId, CancellationToken ct = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(ct);
        var record = NotificationRecord.FromDto(notification, userId);
        dbContext.Set<NotificationRecord>().Add(record);
        // CreationTime 随 DTO 带入（见 FromDto）；CreatorId 由审计拦截器在 SaveChanges 时填充
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<PagedResult<NotificationOutputDto>> GetByUserAsync(
        string userId,
        PageRequest page,
        bool unreadOnly = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var dbContext = await dbContextProvider.GetDbContextAsync(ct);
        var query = dbContext.Set<NotificationRecord>().AsNoTracking().Where(x => x.UserId == userId);
        if (unreadOnly)
        {
            query = query.Where(x => !x.IsRead);
        }

        var totalCount = await query.LongCountAsync(ct);
        var records = await query
            .OrderByDescending(x => x.CreationTime)
            .ThenByDescending(x => x.Id)
            .Skip(page.Offset)
            .Take(page.Limit)
            .ToListAsync(ct);

        return new PagedResult<NotificationOutputDto>(totalCount, records.Select(r => r.ToDto()));
    }

    /// <inheritdoc/>
    public async Task MarkAsReadAsync(string notificationId, string userId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(notificationId, out var id))
            return;

        var dbContext = await dbContextProvider.GetDbContextAsync(ct);
        var record = await dbContext.Set<NotificationRecord>()
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);

        if (record is { IsRead: false })
        {
            record.IsRead = true;
            record.ReadAt = clock.Now;
            await dbContext.SaveChangesAsync(ct);
        }
    }

    /// <inheritdoc/>
    public async Task MarkAllAsReadAsync(string userId, CancellationToken ct = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(ct);
        var records = await dbContext.Set<NotificationRecord>()
            .Where(x => x.UserId == userId && !x.IsRead)
            .ToListAsync(ct);

        if (records.Count == 0)
            return;

        var now = clock.Now;
        foreach (var record in records)
        {
            record.IsRead = true;
            record.ReadAt = now;
        }

        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<int> GetUnreadCountAsync(string userId, CancellationToken ct = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(ct);
        return await dbContext.Set<NotificationRecord>()
            .CountAsync(x => x.UserId == userId && !x.IsRead, ct);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string notificationId, string userId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(notificationId, out var id))
        {
            return false;
        }

        var dbContext = await dbContextProvider.GetDbContextAsync(ct);
        var record = await dbContext.Set<NotificationRecord>().FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (record is null)
        {
            return false;
        }

        dbContext.Set<NotificationRecord>().Remove(record);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>采用加载后 <c>RemoveRange</c> 而非批量删除：兼容所有 EF 提供程序（含内存库），并复用上下文上的拦截器。</remarks>
    public async Task<int> DeleteAllAsync(string userId, CancellationToken ct = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(ct);
        var records = await dbContext.Set<NotificationRecord>().Where(x => x.UserId == userId).ToListAsync(ct);
        if (records.Count == 0)
        {
            return 0;
        }

        dbContext.Set<NotificationRecord>().RemoveRange(records);
        await dbContext.SaveChangesAsync(ct);
        return records.Count;
    }
}
