using Leistd.BackgroundJobs.EntityFrameworkCore.Entities;
using Leistd.BackgroundJobs.Recurring;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.EntityFrameworkCore;

namespace Leistd.BackgroundJobs.EntityFrameworkCore.Stores;

/// <summary>用宿主 DbContext 存储集群周期任务的完成水位，多副本共享。</summary>
/// <remarks>调度器在宿主上下文里读写水位，记录落在宿主库；写入即时保存，不参与业务事务。</remarks>
/// <typeparam name="TDbContext">映射了 <see cref="RecurringJobState"/> 的宿主 DbContext。</typeparam>
/// <param name="dbContextProvider">DbContext 提供器。</param>
public class EfCoreRecurringJobStateStore<TDbContext>(IDbContextProvider<TDbContext> dbContextProvider)
    : IRecurringJobStateStore
    where TDbContext : DbContext
{
    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetLastCompletedSlotAsync(string jobName, CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var slot = await dbContext.Set<RecurringJobState>()
            .AsNoTracking()
            .Where(x => x.Name == jobName)
            .Select(x => (DateTime?)x.LastCompletedSlot)
            .FirstOrDefaultAsync(cancellationToken);

        return slot is { } value ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)) : null;
    }

    /// <inheritdoc />
    public async Task SetLastCompletedSlotAsync(string jobName, DateTimeOffset slot, CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var utc = slot.UtcDateTime;
        var state = await dbContext.Set<RecurringJobState>().FirstOrDefaultAsync(x => x.Name == jobName, cancellationToken);

        if (state is null)
        {
            dbContext.Set<RecurringJobState>().Add(new RecurringJobState { Name = jobName, LastCompletedSlot = utc });
        }
        else if (utc > state.LastCompletedSlot)
        {
            // 水位只前进：较早的时段晚到（跨副本时钟差）不能把已完成的更晚时段覆盖回去
            state.LastCompletedSlot = utc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
