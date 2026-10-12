using System.ComponentModel.DataAnnotations;
using Leistd.Data.Paging;
using Leistd.OperationRecords.Models;

namespace Leistd.OperationRecords.Dtos;

/// <summary>操作记录的查询输出：一条记录按读者裁剪后的形态。</summary>
/// <remarks>
/// <see cref="FailureDetail"/>、<see cref="CorrelationId"/> 与 <see cref="ActorTenantId"/> 只下发给宿主读者，
/// 租户读者拿到的恒为空（服务端裁剪）。
/// </remarks>
public sealed record OperationRecordOutputDto
{
    /// <summary>记录标识。</summary>
    public required Guid Id { get; init; }

    /// <summary>动作码，界面按它渲染句子。</summary>
    public required string Action { get; init; }

    /// <summary>目标标识；没有目标时为 <c>-</c>。</summary>
    public required string TargetId { get; init; }

    /// <summary>目标名快照。</summary>
    public string? TargetName { get; init; }

    /// <summary>授权依据。</summary>
    public required string AuthorizationBasis { get; init; }

    /// <summary>结果。</summary>
    /// <remarks>JSON 里的写法由宿主的枚举转换器决定；模板配置为 camelCase 名称（<c>succeeded</c>、<c>failed</c>）。</remarks>
    public required OperationRecordOutcome Outcome { get; init; }

    /// <summary>发生时间（UTC）。</summary>
    public required DateTime CreationTime { get; init; }

    /// <summary>操作人标识。</summary>
    public string? ActorId { get; init; }

    /// <summary>操作人名快照。</summary>
    public string? ActorName { get; init; }

    /// <summary>目标即操作人：自证类动作成功且记录里没有操作人时为 <see langword="true"/>，界面把目标显示在操作人列。</summary>
    public bool ActorIsTarget { get; init; }

    /// <summary>模拟登录时的真实操作人名。</summary>
    public string? ImpersonatorName { get; init; }

    /// <summary>失败原因码，兼本地化资源键。</summary>
    public string? FailureCode { get; init; }

    /// <summary>失败原因的占位参数（JSON 对象字符串）。</summary>
    public string? FailureData { get; init; }

    /// <summary>
    /// 按当前请求语言渲染的失败原因：查宿主的非泛型 <c>IStringLocalizer</c>，
    /// 用 <see cref="FailureData"/> 填充具名占位符 <c>{Name}</c>。
    /// </summary>
    /// <remarks>
    /// 先查 <c>{FailureCode}:Record</c>（审计专用措辞），取不到再查 <see cref="FailureCode"/> 本身；
    /// 两步各走本地化器的文化回落，审计键在整条回落链（含默认语言）上都未命中才查码本身。
    /// 未注册本地化器、没有失败码或词条缺失时为 <see langword="null"/>，由调用方回落（如显示裸码）。
    /// 参数无法解析时按无参数渲染；没有对应参数的占位符原样保留。
    /// </remarks>
    public string? FailureMessage { get; init; }

    /// <summary>技术说明；仅宿主可见。</summary>
    public string? FailureDetail { get; init; }

    /// <summary>链路标识；仅宿主可见。</summary>
    public string? CorrelationId { get; init; }

    /// <summary>操作发生时的租户；仅宿主可见，宿主上下文里的操作为空。</summary>
    public Guid? ActorTenantId { get; init; }

}

/// <summary>筛选项：对当前读者可见的类别与动作。</summary>
public sealed record OperationRecordFilterOptionsOutputDto
{
    /// <summary>类别，按登记顺序去重。</summary>
    public required IReadOnlyList<string> Categories { get; init; }

    /// <summary>动作。</summary>
    public required IReadOnlyList<OperationActionOptionDto> Actions { get; init; }
}

/// <summary>一个可筛选的动作。</summary>
public sealed record OperationActionOptionDto
{
    /// <summary>动作码。</summary>
    public required string Code { get; init; }

    /// <summary>类别。</summary>
    public required string Category { get; init; }

    /// <summary>严重度。</summary>
    /// <remarks>JSON 里的写法由宿主的枚举转换器决定；模板配置为 camelCase 名称（<c>info</c>、<c>notice</c>、<c>critical</c>）。</remarks>
    public required OperationSeverity Severity { get; init; }
}

/// <summary>分页查询条件。</summary>
/// <remarks>
/// 类别与动作是两个筛选维度：维度内多选取并集，维度之间取交集。
/// 时间按 UTC 比较，两端都是闭区间。
/// </remarks>
public record GetOperationRecordPagedInputDto : PageRequest, IValidatableObject
{
    /// <summary>在动作码、目标标识与操作人名上做包含匹配。</summary>
    [MaxLength(256, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? Keyword { get; init; }

    /// <summary>起始时刻（UTC，含）。</summary>
    public DateTime? StartTime { get; init; }

    /// <summary>结束时刻（UTC，含）。</summary>
    public DateTime? EndTime { get; init; }

    /// <summary>类别，命中任一即匹配。</summary>
    [MaxLength(20, ErrorMessage = "{0} cannot contain more than {1} items.")]
    public IReadOnlyList<string>? Categories { get; init; }

    /// <summary>动作码，命中任一即匹配。</summary>
    [MaxLength(50, ErrorMessage = "{0} cannot contain more than {1} items.")]
    public IReadOnlyList<string>? Actions { get; init; }

    /// <summary>结果；为空不过滤，未定义的枚举值验证失败。</summary>
    /// <remarks>HTTP 端点的查询串取 camelCase 名称（<c>succeeded</c>、<c>failed</c>），由端点转换成本属性。</remarks>
    [EnumDataType(typeof(OperationRecordOutcome), ErrorMessage = "{0} is not an allowed value.")]
    public OperationRecordOutcome? Outcome { get; init; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (StartTime.HasValue && EndTime.HasValue && StartTime > EndTime)
        {
            yield return new ValidationResult(
                "The start time must not be later than the end time.",
                [nameof(StartTime), nameof(EndTime)]);
        }
    }
}

/// <summary>导出条件：与分页查询同一组筛选，另有导出条数上限。</summary>
public sealed record ExportOperationRecordsInputDto : IValidatableObject
{
    /// <summary>单次导出的最大条数。</summary>
    public const int MaximumExportCount = 10000;

    /// <summary>在动作码、目标标识与操作人名上做包含匹配。</summary>
    [MaxLength(256, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? Keyword { get; init; }

    /// <summary>起始时刻（UTC，含）。</summary>
    public DateTime? StartTime { get; init; }

    /// <summary>结束时刻（UTC，含）。</summary>
    public DateTime? EndTime { get; init; }

    /// <summary>类别，命中任一即匹配。</summary>
    [MaxLength(20, ErrorMessage = "{0} cannot contain more than {1} items.")]
    public IReadOnlyList<string>? Categories { get; init; }

    /// <summary>动作码，命中任一即匹配。</summary>
    [MaxLength(50, ErrorMessage = "{0} cannot contain more than {1} items.")]
    public IReadOnlyList<string>? Actions { get; init; }

    /// <summary>结果；为空不过滤，未定义的枚举值验证失败。</summary>
    /// <remarks>HTTP 端点的查询串取 camelCase 名称（<c>succeeded</c>、<c>failed</c>），由端点转换成本属性。</remarks>
    [EnumDataType(typeof(OperationRecordOutcome), ErrorMessage = "{0} is not an allowed value.")]
    public OperationRecordOutcome? Outcome { get; init; }

    /// <summary>导出条数，1–<see cref="MaximumExportCount"/>，默认取上限。</summary>
    [Range(1, MaximumExportCount, ErrorMessage = "{0} must be between {1} and {2}.")]
    public int Limit { get; init; } = MaximumExportCount;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (StartTime.HasValue && EndTime.HasValue && StartTime > EndTime)
        {
            yield return new ValidationResult(
                "The start time must not be later than the end time.",
                [nameof(StartTime), nameof(EndTime)]);
        }
    }
}

/// <summary>导出文件。</summary>
/// <param name="Content">文件内容（带 BOM 的 UTF-8 CSV）。</param>
/// <param name="ContentType">内容类型。</param>
/// <param name="FileName">建议的文件名。</param>
public sealed record OperationRecordExportFileDto(byte[] Content, string ContentType, string FileName);

/// <summary>导出本身的审计：动作码与授权依据由宿主定义，组件只负责在导出成功后记下这一笔。</summary>
/// <param name="Action">已登记的动作码（如 <c>operation-records.exported</c>）。</param>
/// <param name="AuthorizationBasis">授权依据，通常是导出权限名。</param>
public sealed record OperationRecordExportAudit(string Action, string AuthorizationBasis);
