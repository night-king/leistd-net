namespace Leistd.Notifications.Dtos;

/// <summary>要发布的通知内容，尚未归属到任何收件人。</summary>
/// <remarks>发布器为每个收件人创建独立的 <see cref="NotificationOutputDto"/>，确定记录标识、创建时间与未读状态。</remarks>
public record NotificationInputDto
{
    /// <summary>通知标题。</summary>
    public required string Title { get; init; }

    /// <summary>通知内容（可选）。</summary>
    public string? Content { get; init; }

    /// <summary>未指定类型时的通知类型；其余类型由业务项目定义。</summary>
    public const string DefaultType = "System";

    /// <summary>通知类型，通知偏好与前端图标按它区分；默认 <see cref="DefaultType"/>。</summary>
    public string Type { get; init; } = DefaultType;

    /// <summary>点击跳转路由（可选）。</summary>
    public string? Link { get; init; }

    /// <summary>图标标识（可选，由前端解释）。</summary>
    public string? Icon { get; init; }

    /// <summary>关联实体 ID（可选）。</summary>
    public string? RelatedEntityId { get; init; }

    /// <summary>关联实体类型（可选）。</summary>
    public string? RelatedEntityType { get; init; }

    /// <summary>扩展元数据（可选）。</summary>
    public Dictionary<string, object>? Metadata { get; init; }
}
