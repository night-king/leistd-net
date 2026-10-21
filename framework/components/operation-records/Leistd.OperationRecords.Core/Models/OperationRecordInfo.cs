using Leistd.Tracing.Constants;
using Leistd.OperationRecords.Definitions;

namespace Leistd.OperationRecords.Models;

/// <summary>一条操作记录：什么人、在什么时间、凭什么、做了什么、结果如何。</summary>
/// <remarks>
/// 请求维度的信息（IP、UA、URL）与变更明细不在这里，按 <see cref="CorrelationId"/> 回查请求日志。
/// </remarks>
public sealed class OperationRecordInfo
{
    /// <summary>动作码长度上限。</summary>
    public const int MaxActionLength = 120;

    /// <summary>目标标识长度上限。</summary>
    public const int MaxTargetIdLength = 160;

    /// <summary>授权依据长度上限。</summary>
    public const int MaxAuthorizationBasisLength = 160;

    /// <summary>操作人名长度上限。</summary>
    public const int MaxActorNameLength = 160;

    /// <summary>操作人标识长度上限：容得下 GUID 字符串与机器主体的 client_id。</summary>
    public const int MaxActorIdLength = 128;

    /// <summary>链路标识长度上限。</summary>
    public const int MaxCorrelationIdLength = CorrelationIdConstants.MaxLength;

    /// <summary>目标名快照长度上限：与操作人名同量级，两者都是显示名。</summary>
    public const int MaxTargetNameLength = 160;

    /// <summary>失败错误码长度上限：与授权依据同量级，两者都是 <c>Xxx:Yyy</c> 形态的码。</summary>
    public const int MaxFailureCodeLength = 160;

    /// <summary>失败技术说明长度上限。</summary>
    public const int MaxFailureDetailLength = 1024;

    /// <summary>记录标识（有序 Guid v7，本身即按写入时间单调）。</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>记录所在的层：<see langword="null"/> 表示宿主层，否则是该租户的层。</summary>
    /// <remarks>
    /// 通常就是操作发生时的租户上下文；例外是 <see cref="OperationVisibility.Host"/> 的失败记录，
    /// 一律写进宿主层（此值为 <see langword="null"/>），来源租户由 <see cref="ActorTenantId"/> 保留，宿主才能看到它。
    /// </remarks>
    public Guid? TenantId { get; init; }

    /// <summary>操作人所属的租户（主体的租户声明）；<see langword="null"/> 表示宿主主体。</summary>
    /// <remarks>
    /// <see cref="ActorId"/> 只在它所属的租户里有意义。取操作人自己的租户而不是当时的上下文
    /// （宿主管理员进入租户操作时两者不同）；匿名请求取请求所在的租户上下文。
    /// </remarks>
    public Guid? ActorTenantId { get; init; }

    /// <summary>做了什么：业务动作码，如 <c>identity.user.created</c>。</summary>
    /// <remarks>
    /// 由业务定义并保持稳定，界面按它本地化；不要用 HTTP 方法与路由代替。
    /// </remarks>
    public required string Action { get; init; }

    /// <summary>对谁做的：目标标识；没有目标时为 <c>-</c>。</summary>
    public required string TargetId { get; init; }

    /// <summary>对谁做的：目标名快照；取不到时为 <see langword="null"/>。</summary>
    /// <remarks>授权阶段的拒绝不回填名字：调用方无权查看该目标。</remarks>
    public string? TargetName { get; init; }

    /// <summary>凭什么：授权依据，由业务定义。</summary>
    /// <remarks>
    /// 通常是权限名；不由权限把守的操作（改自己的密码、机器主体凭 scope 调用）传业务自己的标记。
    /// </remarks>
    public required string AuthorizationBasis { get; init; }

    /// <summary>结果如何。</summary>
    public required OperationRecordOutcome Outcome { get; init; }

    /// <summary>这条记录能被谁看见，写入时取自 <see cref="IOperationActionDefinition.Visibility"/>；必填。</summary>
    /// <remarks>复制到记录上，查询才能在数据库里按它过滤并正确分页。</remarks>
    public required OperationVisibility Visibility { get; init; }

    /// <summary>为什么没成：失败原因的稳定错误码，兼本地化资源键。</summary>
    /// <remarks>存码不存句子，展示时按读者当前语言渲染。</remarks>
    public string? FailureCode { get; init; }

    /// <summary>失败原因的本地化占位参数，序列化为 JSON 对象。</summary>
    public string? FailureData { get; init; }

    /// <summary>面向排查的技术说明，不本地化。</summary>
    /// <remarks>
    /// 仅宿主可见；内容须是显式传入的可公开说明，不是原始异常文本（见 <see cref="OperationFailure"/>）。
    /// </remarks>
    public string? FailureDetail { get; init; }

    /// <summary>什么时间（UTC）。</summary>
    public DateTime CreationTime { get; init; }

    /// <summary>什么人：操作人标识，取主体声明的原始值；匿名请求为 <see langword="null"/>。</summary>
    /// <remarks>
    /// 不要求是 GUID：机器主体（<c>client:&lt;client_id&gt;</c>）与后台作业主体的 <c>sub</c> 原样记下。
    /// </remarks>
    public string? ActorId { get; init; }

    /// <summary>什么人：操作人显示名快照，改名或销号后仍保留当时的名字。</summary>
    public string? ActorName { get; init; }

    /// <summary>模拟登录时的真实操作人标识；非模拟场景为 <see langword="null"/>。</summary>
    /// <remarks>有值时 <see cref="ActorId"/> 是被模拟的用户，这里是实际操作的人。</remarks>
    public string? ImpersonatorId { get; init; }

    /// <summary>模拟登录时真实操作人的显示名快照。</summary>
    public string? ImpersonatorName { get; init; }

    /// <summary>链路标识，与请求日志、响应头同源，用于从记录反查当次请求的全部细节。</summary>
    public string? CorrelationId { get; init; }
}
