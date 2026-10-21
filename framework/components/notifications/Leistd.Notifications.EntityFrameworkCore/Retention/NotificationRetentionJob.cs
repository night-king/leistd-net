using System.Reflection;
using Leistd.BackgroundJobs.Recurring;
using Leistd.Data.Connections;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.Notifications.EntityFrameworkCore.Entities;
using Leistd.Notifications.EntityFrameworkCore.Options;
using Leistd.Timing;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Leistd.Notifications.EntityFrameworkCore.Retention;

// 按物理库逐个清理：IgnoreQueryFilters 只能放开同一个库里的租户。
// 开关在登记时交给调度器（关闭时到点跳过、不记完成）；读 IOptionsSnapshot，与判开关用的是本轮同一份配置。
// 每批一个工作单元；有库失败时抛出，调度器不记水位，下一个调度时段按截止时间扫描重做。
internal sealed class NotificationRetentionJob<TDbContext>(
    IOptionsSnapshot<NotificationRetentionOptions> retention,
    ITenantDatabaseRunner databaseRunner,
    IUnitOfWorkManager unitOfWorkManager,
    IDbContextProvider<TDbContext> dbContextProvider,
    IClock clock,
    ILogger<NotificationRetentionJob<TDbContext>> logger) : IRecurringJob
    where TDbContext : DbContext
{
    internal const string Name = "notifications.retention";

    private static readonly string ConnectionStringName =
        typeof(TDbContext).GetCustomAttribute<ConnectionStringNameAttribute>()?.Name ?? ConnectionStringNames.Default;

    public async Task ExecuteAsync(RecurringJobContext context, CancellationToken cancellationToken)
    {
        var current = retention.Value;
        var now = clock.Now;
        var readCutoff = now.AddDays(-current.ReadRetentionDays);
        var unreadCutoff = now.AddDays(-current.UnreadRetentionDays);
        var deleted = 0;

        // 停用租户的库照样清理
        var result = await databaseRunner.ForEachDatabaseAsync(ConnectionStringName, activeOnly: false, async (_, ct) =>
        {
            deleted += await DeleteCurrentDatabaseAsync(readCutoff, unreadCutoff, current.BatchSize, ct);
        }, cancellationToken);

        // 解析不出连接的租户与失败的库一样让本轮失败；调度器不记水位，本时段仍可被其他副本重试
        if (result.FailedDatabases.Count > 0 || result.UnresolvedTenants.Count > 0)
        {
            throw new InvalidOperationException(
                $"Deleted {deleted} expired notification(s); {result.FailedDatabases.Count} of {result.Databases} database(s) failed, " +
                $"{result.UnresolvedTenants.Count} tenant(s) could not be resolved to a database.");
        }

        logger.LogInformation("Deleted {Count} expired notification(s) across {Databases} database(s).", deleted, result.Databases);
    }

    private async Task<int> DeleteCurrentDatabaseAsync(DateTime readCutoff, DateTime unreadCutoff, int batchSize, CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var unitOfWork = unitOfWorkManager.Begin(requiresNew: true);
            var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);

            // IgnoreQueryFilters：同库其余租户的通知一并清理
            var batch = await dbContext.Set<NotificationRecord>()
                .IgnoreQueryFilters()
                .Where(x => x.CreationTime < unreadCutoff || (x.IsRead && x.CreationTime < readCutoff))
                .OrderBy(x => x.CreationTime)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                return total;
            }

            dbContext.Set<NotificationRecord>().RemoveRange(batch);
            await unitOfWork.CompleteAsync(cancellationToken);

            total += batch.Count;
            if (batch.Count < batchSize)
            {
                return total;
            }
        }
    }
}
