using Leistd.OperationRecords.Models;

namespace Leistd.OperationRecords.EntityFrameworkCore.Entities;

/// <summary>操作记录的归档形态：列与 <see cref="OperationRecord"/> 逐一对齐，外加一列归档时刻。</summary>
/// <remarks>
/// <para>不实现 <c>IMultiTenant</c>：归档作业跑在无租户过滤的查询里。<see cref="TenantId"/> 是普通列，
/// 查询归档表时须自行按租户过滤。</para>
/// <para>列定义须与原表一致：搬运逐字段复制，缺列会静默丢数据。</para>
/// </remarks>
public class OperationRecordArchive
{
    /// <summary>主键：沿用原记录的 Id，便于按同一标识回溯。</summary>
    public Guid Id { get; set; }

    /// <summary>原记录所在的层；不是多租户过滤维度，见类型说明。</summary>
    public Guid? TenantId { get; set; }

    /// <summary>操作人所属的租户。</summary>
    public Guid? ActorTenantId { get; set; }

    /// <summary>业务动作码。</summary>
    public string Action { get; set; } = default!;

    /// <summary>操作目标标识；无目标时为 <c>-</c>。</summary>
    public string TargetId { get; set; } = default!;

    /// <summary>授权依据。</summary>
    public string AuthorizationBasis { get; set; } = default!;

    /// <summary>操作结果。</summary>
    public OperationRecordOutcome Outcome { get; set; }

    /// <summary>原记录的发生时间（UTC）。</summary>
    public DateTime CreationTime { get; set; }

    /// <summary>搬入归档表的时刻（UTC）。</summary>
    public DateTime ArchivedTime { get; set; }

    /// <summary>操作人标识。</summary>
    public string? ActorId { get; set; }

    /// <summary>操作人显示名快照。</summary>
    public string? ActorName { get; set; }

    /// <summary>模拟登录时的真实操作人标识。</summary>
    public string? ImpersonatorId { get; set; }

    /// <summary>模拟登录时真实操作人的显示名快照。</summary>
    public string? ImpersonatorName { get; set; }

    /// <summary>链路标识。</summary>
    public string? CorrelationId { get; set; }

    /// <summary>操作目标的人类可读名快照。</summary>
    public string? TargetName { get; set; }

    /// <summary>这条记录原本能被谁看见。</summary>
    public OperationVisibility Visibility { get; set; }

    /// <summary>失败原因的稳定错误码。</summary>
    public string? FailureCode { get; set; }

    /// <summary>失败原因的本地化占位参数（JSON）。</summary>
    public string? FailureData { get; set; }

    /// <summary>面向排查的技术说明。</summary>
    public string? FailureDetail { get; set; }

    internal static OperationRecordArchive From(OperationRecord record, DateTime archivedTime) => new()
    {
        Id = record.Id,
        TenantId = record.TenantId,
        ActorTenantId = record.ActorTenantId,
        Action = record.Action,
        TargetId = record.TargetId,
        AuthorizationBasis = record.AuthorizationBasis,
        Outcome = record.Outcome,
        CreationTime = record.CreationTime,
        ArchivedTime = archivedTime,
        ActorId = record.ActorId,
        ActorName = record.ActorName,
        ImpersonatorId = record.ImpersonatorId,
        ImpersonatorName = record.ImpersonatorName,
        CorrelationId = record.CorrelationId,
        TargetName = record.TargetName,
        Visibility = record.Visibility,
        FailureCode = record.FailureCode,
        FailureData = record.FailureData,
        FailureDetail = record.FailureDetail
    };
}
