using Leistd.OperationRecords.Stores;

namespace Leistd.OperationRecords.Models;

/// <summary>查询时的可见范围：由调用方算好，<see cref="IOperationRecordReader"/> 只照做，不判定读者是否为宿主。</summary>
/// <remarks>
/// <para>跨租户隔离由 <c>IMultiTenant</c> 的全局查询过滤器承担，本类型只在同一层内部再按可见性筛选。</para>
/// <para>没有默认值：实例只能从 <see cref="Host"/>、<see cref="ForTenantReader"/> 与 <see cref="Unrestricted"/> 取得，
/// 不过滤只能显式写出。</para>
/// </remarks>
public sealed class OperationRecordVisibilityScope
{
    private OperationRecordVisibilityScope(bool restricted, bool includesHostRecords, string? actorId, Guid? actorTenantId)
    {
        IsRestricted = restricted;
        IncludesHostRecords = includesHostRecords;
        ActorId = actorId;
        ActorTenantId = actorTenantId;
    }

    /// <summary>是否施加可见性限制；仅 <see cref="Unrestricted"/> 为 <see langword="false"/>。</summary>
    public bool IsRestricted { get; }

    /// <summary>读者能否看到 <see cref="OperationVisibility.Host"/> 层的记录。</summary>
    public bool IncludesHostRecords { get; }

    /// <summary>读者自身的操作人标识，用于放行 <see cref="OperationVisibility.Actor"/> 层的记录；为 <see langword="null"/> 时该层一律不可见。</summary>
    public string? ActorId { get; }

    /// <summary>读者自身所属的租户（宿主主体为 <see langword="null"/>），与 <see cref="ActorId"/> 一起认定"本人"。</summary>
    /// <remarks>主体标识只在签发它的那一层内唯一，因此“本人”须标识与所属租户都相同。</remarks>
    public Guid? ActorTenantId { get; }

    /// <summary>宿主视角：所有层级都可见。</summary>
    public static OperationRecordVisibilityScope Host { get; } = new(true, true, null, null);

    /// <summary>不加可见性限制，供不代表某个读者的内部任务使用（如归档、导出到运维系统）。</summary>
    /// <remarks>
    /// 面向用户列出记录时应使用 <see cref="Host"/> 或 <see cref="ForTenantReader"/>。
    /// 只给出聚合数、不下发单条记录的统计（如本层的活跃用户数）可用它：可见性约束的是谁能看到哪条记录。
    /// </remarks>
    public static OperationRecordVisibilityScope Unrestricted { get; } = new(false, true, null, null);

    /// <summary>租户视角：看不到 <c>Host</c> 层；<c>Actor</c> 层仅当记录的操作人是本人（标识与所属租户都相同）。</summary>
    /// <param name="actorId">读者自身的操作人标识；未知时传 <see langword="null"/>。</param>
    /// <param name="actorTenantId">读者自身所属的租户；宿主主体传 <see langword="null"/>。</param>
    public static OperationRecordVisibilityScope ForTenantReader(string? actorId, Guid? actorTenantId)
        => new(true, false, string.IsNullOrWhiteSpace(actorId) ? null : actorId.Trim(), actorTenantId);
}
