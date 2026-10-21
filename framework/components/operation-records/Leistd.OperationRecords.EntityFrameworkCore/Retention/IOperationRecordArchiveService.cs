namespace Leistd.OperationRecords.EntityFrameworkCore.Retention;

/// <summary>把到期的操作记录搬入归档表。</summary>
/// <remarks>
/// 与只增不减的存储契约分开，删除能力只在这里。周期任务按保留期自动调用，宿主也可手动调用。
/// </remarks>
public interface IOperationRecordArchiveService
{
    /// <summary>在宿主库与每个独立库里，把创建时间早于 <paramref name="cutoffUtc"/> 的记录分批搬入归档表。</summary>
    /// <remarks>每批一个事务，写归档与删原表一起提交；某个库失败只记日志并计入结果，其余库照常执行。
    /// 解析不出连接的租户同样被跳过并计入结果。</remarks>
    /// <param name="cutoffUtc">截止时刻（UTC，不含）。</param>
    /// <param name="batchSize">单批条数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<OperationRecordArchiveResult> ArchiveOlderThanAsync(
        DateTime cutoffUtc,
        int batchSize,
        CancellationToken cancellationToken = default);
}

/// <summary>一次归档的结果。</summary>
/// <param name="Archived">搬走的总条数。</param>
/// <param name="Databases">处理的物理库数（含宿主库）。</param>
/// <param name="FailedDatabases">失败的库数；对应的错误日志里有库的指纹。</param>
/// <param name="UnresolvedTenants">解析不出连接、本轮没被归档的租户数，与 <paramref name="FailedDatabases"/> 分开计。</param>
public sealed record OperationRecordArchiveResult(
    int Archived, int Databases, int FailedDatabases, int UnresolvedTenants = 0);
