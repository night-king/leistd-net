using Leistd.Data.Paging;
using Leistd.OperationRecords.Definitions;
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
    /// 筛选条件的语义见 <see cref="OperationRecordFilter"/>；分页只作用于取条目，总数按同一组筛选条件计算。
    /// <see cref="PageRequest.Sorting"/> 不生效。
    /// </remarks>
    /// <param name="filter">筛选条件。</param>
    /// <param name="page">分页。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<PagedResult<OperationRecordInfo>> GetPagedListAsync(
        OperationRecordFilter filter,
        PageRequest page,
        CancellationToken cancellationToken = default);

    /// <summary>按发生时间的 UTC 日统计当前租户层的记录数。</summary>
    /// <remarks>筛选条件的语义见 <see cref="OperationRecordCountFilter"/>；没有记录的日子不出现在结果里。</remarks>
    /// <param name="filter">筛选条件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentOutOfRangeException">起始时刻晚于结束时刻。</exception>
    Task<IReadOnlyDictionary<DateOnly, int>> CountByDayAsync(
        OperationRecordCountFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>统计当前租户层符合条件的记录涉及的不同目标数。</summary>
    /// <remarks>
    /// 筛选条件的语义见 <see cref="OperationRecordCountFilter"/>；目标按标识原文去重，
    /// 没有目标的记录（<see cref="OperationTarget.NoTargetId"/>）不计入。
    /// </remarks>
    /// <param name="filter">筛选条件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentOutOfRangeException">起始时刻晚于结束时刻。</exception>
    Task<int> CountDistinctTargetsAsync(
        OperationRecordCountFilter filter,
        CancellationToken cancellationToken = default);
}

/// <summary>操作记录的存储层筛选条件。</summary>
/// <remarks>
/// <para>时间按 UTC 比较，两端都是闭区间；按展示时区选的日期由调用方换算成 UTC。</para>
/// <para>不按类别过滤：类别定义在 <see cref="IOperationActionDefinition"/> 上，调用方展开成动作码集合传入 <see cref="Actions"/>。</para>
/// </remarks>
public sealed record OperationRecordFilter
{
    /// <summary>可见范围，由调用方算好；必填，不过滤须显式传 <see cref="OperationRecordVisibilityScope.Unrestricted"/>。</summary>
    public required OperationRecordVisibilityScope Scope { get; init; }

    /// <summary>在动作码、目标标识与操作人名上做包含匹配；为空不过滤。</summary>
    public string? Keyword { get; init; }

    /// <summary>起始时刻（含），UTC；为空不设下界。</summary>
    public DateTime? StartTime { get; init; }

    /// <summary>结束时刻（含），UTC；为空不设上界。</summary>
    public DateTime? EndTime { get; init; }

    /// <summary>按动作码过滤，命中任一即匹配；<see langword="null"/> 或空集合表示不过滤。</summary>
    public IReadOnlyCollection<string>? Actions { get; init; }

    /// <summary>按结果过滤；<see langword="null"/> 表示不过滤。</summary>
    public OperationRecordOutcome? Outcome { get; init; }
}

/// <summary>操作记录计数的筛选条件：一个时间区间内、按可见范围与动作筛出的记录。</summary>
/// <remarks>
/// <para>时间区间是 UTC 的半开区间 [<see cref="StartTime"/>, <see cref="EndTime"/>)：相邻区间首尾相接时，
/// 恰好落在边界上的记录只归后一个区间。这与列表筛选 <see cref="OperationRecordFilter"/> 的闭区间不同，
/// 统计按区间切分，闭区间会让边界记录同时计入两边。</para>
/// <para>两端都必填：计数覆盖归档，不设界会扫过全部历史。</para>
/// </remarks>
public sealed record OperationRecordCountFilter
{
    /// <summary>可见范围，由调用方算好；必填，不过滤须显式传 <see cref="OperationRecordVisibilityScope.Unrestricted"/>。</summary>
    public required OperationRecordVisibilityScope Scope { get; init; }

    /// <summary>起始时刻（含），UTC。</summary>
    public required DateTime StartTime { get; init; }

    /// <summary>结束时刻（不含），UTC；不得早于 <see cref="StartTime"/>，两者相等时结果为空。</summary>
    public required DateTime EndTime { get; init; }

    /// <summary>按动作码过滤，命中任一即匹配；<see langword="null"/> 或空集合表示不过滤。</summary>
    public IReadOnlyCollection<string>? Actions { get; init; }
}
