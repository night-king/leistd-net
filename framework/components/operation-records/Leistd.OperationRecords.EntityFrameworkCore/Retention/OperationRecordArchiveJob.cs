using Leistd.BackgroundJobs.Recurring;
using Leistd.OperationRecords.EntityFrameworkCore.Options;
using Leistd.Timing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Leistd.OperationRecords.EntityFrameworkCore.Retention;

// 保留期归档的周期任务。开关在登记时交给调度器（关闭时到点跳过、不记完成，打开后下一轮即生效）；
// 读 IOptionsSnapshot：与调度器判开关用的是本轮作用域里同一份配置，不会按“开启”放行后拿到热更新的“关闭”参数
internal sealed class OperationRecordArchiveJob(
    IOptionsSnapshot<OperationRecordRetentionOptions> retention,
    IOperationRecordArchiveService archiveService,
    IClock clock,
    ILogger<OperationRecordArchiveJob> logger) : IRecurringJob
{
    internal const string Name = "operation-records.archive";

    public async Task ExecuteAsync(RecurringJobContext context, CancellationToken cancellationToken)
    {
        // 越界的设置值在读取快照时抛校验异常（判开关时就会先抛），按本轮失败记日志，不搬任何记录。
        // 走到这里时本轮快照是开启的：启用时天数必填已由选项校验保证
        var current = retention.Value;
        var cutoff = clock.Now.AddDays(-current.RetentionDays!.Value);
        var result = await archiveService.ArchiveOlderThanAsync(cutoff, current.BatchSize, cancellationToken);

        // 解析不出连接的租户与失败的库一样让本轮失败
        if (result.FailedDatabases > 0 || result.UnresolvedTenants > 0)
        {
            // 抛出让调度器不记水位：本时段仍可被其他副本重试，最迟在下一个调度时段重做
            // （按截止时间扫描，积压会一并搬走）
            throw new InvalidOperationException(
                $"Archived {result.Archived} operation record(s) created before {cutoff:o}; " +
                $"{result.FailedDatabases} of {result.Databases} database(s) failed, " +
                $"{result.UnresolvedTenants} tenant(s) could not be resolved to a database.");
        }

        logger.LogInformation(
            "Archived {Count} operation record(s) created before {Cutoff:o} across {Databases} database(s).",
            result.Archived, cutoff, result.Databases);
    }
}
