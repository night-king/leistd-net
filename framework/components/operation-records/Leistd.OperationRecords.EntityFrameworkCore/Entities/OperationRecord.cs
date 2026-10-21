using Leistd.MultiTenancy.Tenancy;
using Leistd.OperationRecords.Models;

namespace Leistd.OperationRecords.EntityFrameworkCore.Entities;

/// <summary>操作记录的持久化形态。</summary>
/// <remarks>
/// <para>不实现 <c>ICreationAuditedObject</c>：操作人与时间由记录器填充，不依赖宿主的审计拦截器。</para>
/// <para>写入后不再修改：没有修改与删除审计列，也没有对应的存储方法。</para>
/// </remarks>
public class OperationRecord : IMultiTenant
{
    /// <summary>主键（有序 Guid v7）。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid? TenantId { get; set; }

    /// <summary>操作人所属的租户；<see langword="null"/> 表示宿主主体。</summary>
    public Guid? ActorTenantId { get; set; }

    /// <summary>业务动作码。</summary>
    public string Action { get; set; } = default!;

    /// <summary>操作目标标识；无目标时为 <c>-</c>。</summary>
    public string TargetId { get; set; } = default!;

    /// <summary>授权依据：权限名，或非权限体系的标记。</summary>
    public string AuthorizationBasis { get; set; } = default!;

    /// <summary>操作结果。</summary>
    public OperationRecordOutcome Outcome { get; set; }

    /// <summary>发生时间（UTC）。</summary>
    public DateTime CreationTime { get; set; }

    /// <summary>操作人标识；匿名请求为 <see langword="null"/>。</summary>
    public string? ActorId { get; set; }

    /// <summary>操作人显示名的快照。</summary>
    public string? ActorName { get; set; }

    /// <summary>模拟登录时的真实操作人标识。</summary>
    public string? ImpersonatorId { get; set; }

    /// <summary>模拟登录时真实操作人的显示名快照。</summary>
    public string? ImpersonatorName { get; set; }

    /// <summary>链路标识，用于回查当次请求的完整日志。</summary>
    public string? CorrelationId { get; set; }

    /// <summary>操作目标的人类可读名快照；取不到时为 <see langword="null"/>。</summary>
    public string? TargetName { get; set; }

    /// <summary>这条记录能被谁看见；写入时由动作定义盖章，查询按它过滤。</summary>
    public OperationVisibility Visibility { get; set; } = OperationVisibility.Host;

    /// <summary>失败原因的稳定错误码，兼本地化资源键。</summary>
    public string? FailureCode { get; set; }

    /// <summary>失败原因的本地化占位参数（JSON）。</summary>
    public string? FailureData { get; set; }

    /// <summary>面向排查的技术说明，仅宿主可见。</summary>
    public string? FailureDetail { get; set; }

    /// <summary>由传输形态创建实体。</summary>
    /// <param name="info">记录内容。</param>
    public static OperationRecord FromInfo(OperationRecordInfo info) => new()
    {
        Id = info.Id,
        TenantId = info.TenantId,
        ActorTenantId = info.ActorTenantId,
        Action = Truncate(info.Action, OperationRecordInfo.MaxActionLength)!,
        TargetId = Truncate(info.TargetId, OperationRecordInfo.MaxTargetIdLength)!,
        AuthorizationBasis = Truncate(info.AuthorizationBasis, OperationRecordInfo.MaxAuthorizationBasisLength)!,
        Outcome = info.Outcome,
        CreationTime = info.CreationTime,
        ActorId = Truncate(info.ActorId, OperationRecordInfo.MaxActorIdLength),
        ActorName = Truncate(info.ActorName, OperationRecordInfo.MaxActorNameLength),
        ImpersonatorId = Truncate(info.ImpersonatorId, OperationRecordInfo.MaxActorIdLength),
        ImpersonatorName = Truncate(info.ImpersonatorName, OperationRecordInfo.MaxActorNameLength),
        CorrelationId = Truncate(info.CorrelationId, OperationRecordInfo.MaxCorrelationIdLength),
        TargetName = Truncate(info.TargetName, OperationRecordInfo.MaxTargetNameLength),
        Visibility = info.Visibility,
        FailureCode = Truncate(info.FailureCode, OperationRecordInfo.MaxFailureCodeLength),
        // FailureData 不截断：截断的 JSON 无法解析
        FailureData = info.FailureData,
        FailureDetail = Truncate(info.FailureDetail, OperationRecordInfo.MaxFailureDetailLength)
    };

    /// <summary>转换为传输形态。</summary>
    public OperationRecordInfo ToInfo() => new()
    {
        Id = Id,
        TenantId = TenantId,
        ActorTenantId = ActorTenantId,
        Action = Action,
        TargetId = TargetId,
        AuthorizationBasis = AuthorizationBasis,
        Outcome = Outcome,
        CreationTime = CreationTime,
        ActorId = ActorId,
        ActorName = ActorName,
        ImpersonatorId = ImpersonatorId,
        ImpersonatorName = ImpersonatorName,
        CorrelationId = CorrelationId,
        TargetName = TargetName,
        Visibility = Visibility,
        FailureCode = FailureCode,
        FailureData = FailureData,
        FailureDetail = FailureDetail
    };

    /// <summary>操作人标识列长度上限：容得下 GUID 字符串与机器主体的 client_id。</summary>
    /// <remarks>引用 <see cref="OperationRecordInfo.MaxActorIdLength"/>，截断长度与列长度同源。</remarks>
    public const int MaxActorIdLength = OperationRecordInfo.MaxActorIdLength;

    // 就地截断而不是抛异常，不让超长字段把成功的业务变成 500；记录器写入前已记 Warning
    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
