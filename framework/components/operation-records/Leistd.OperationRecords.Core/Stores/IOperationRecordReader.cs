using Leistd.Data.Paging;
using Leistd.OperationRecords.Models;

namespace Leistd.OperationRecords.Stores;

/// <summary>操作记录的历史读取契约。</summary>
/// <remarks>
/// <para>只由能回读历史的存储实现（如数据库存储）；只写不读的输出适配不实现本接口，也就不注册查询、导出与归档。</para>
/// <para>只读当前租户层：租户隔离由实现按当前租户上下文与数据过滤器承担，因此本契约不带租户参数。</para>
/// <para>分页只读未归档的记录；计数覆盖存储保存的全部历史，含保留期已搬走的记录，按统计区间回看时不因归档而变少。</para>
/// </remarks>
public interface IOperationRecordReader
{
    /// <summary>按创建时间倒序分页查询当前租户的记录。</summary>
    /// <remarks>
    /// 查询条件的语义见 <see cref="OperationRecordCriteria"/>；分页只作用于取条目，总数按同一组条件计算。
    /// <see cref="PageRequest.Sorting"/> 不生效。
    /// </remarks>
    /// <param name="criteria">查询条件。</param>
    /// <param name="page">分页。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<PagedResult<OperationRecordInfo>> GetPagedListAsync(
        OperationRecordCriteria criteria,
        PageRequest page,
        CancellationToken cancellationToken = default);

    /// <summary>按调用方给定的 UTC 桶边界统计当前租户层的记录数或不同目标数。</summary>
    /// <remarks>
    /// <para>第 i 个结果对应 UTC 半开区间 [<paramref name="edges"/>[i], <paramref name="edges"/>[i+1])：恰好落在边界上的记录
    /// 只归后一个桶；没有记录的桶为 0，结果长度恒为边界数减一。桶怎么切（自然日、本地小时）由调用方按自己的日历与时区算好，
    /// 可以不等长，夏令时当天的日桶就是 23 或 25 小时。</para>
    /// <para>只传首尾两个边界即得整段区间的总数；按目标去重时各桶分别去重，桶数相加不等于区间去重数。</para>
    /// </remarks>
    /// <param name="scope">可见范围，由调用方算好；不过滤须显式传 <see cref="OperationRecordVisibilityScope.Unrestricted"/>。</param>
    /// <param name="edges">桶边界，UTC；至少两个且严格递增，首尾即统计区间。</param>
    /// <param name="actions">按动作码过滤，命中任一即计入；<see langword="null"/> 或空集合表示不过滤。</param>
    /// <param name="distinctTargets">
    /// 为 <see langword="true"/> 时数的是不同目标数：目标按标识原文去重，没有目标的记录（<see cref="OperationTarget.NoTargetId"/>）不计入。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentException">边界少于两个，或不是严格递增。</exception>
    Task<IReadOnlyList<int>> CountByBucketsAsync(
        OperationRecordVisibilityScope scope,
        IReadOnlyList<DateTime> edges,
        IReadOnlyCollection<string>? actions = null,
        bool distinctTargets = false,
        CancellationToken cancellationToken = default);
}
