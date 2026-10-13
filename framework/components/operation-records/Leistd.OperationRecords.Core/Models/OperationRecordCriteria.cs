using Leistd.OperationRecords.Definitions;
using Leistd.OperationRecords.Stores;

namespace Leistd.OperationRecords.Models;

/// <summary>按条件分页查询操作记录时的查询条件，供 <see cref="IOperationRecordReader.GetPagedListAsync"/> 使用。</summary>
/// <remarks>
/// <para>时间按 UTC 比较，两端都是闭区间；按展示时区选的日期由调用方换算成 UTC。</para>
/// <para>不按类别过滤：类别定义在 <see cref="IOperationActionDefinition"/> 上，调用方展开成动作码集合传入 <see cref="Actions"/>。</para>
/// </remarks>
public sealed record OperationRecordCriteria
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
