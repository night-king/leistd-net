namespace Leistd.Notifications.Dtos;

/// <summary>归属到某个收件人的一条通知。</summary>
public record NotificationOutputDto
{
    /// <summary>该用户这条通知记录的 ID，标记已读时使用。</summary>
    /// <remarks>必填：只由发布器按收件人定案，或持久化读取时映射回来。</remarks>
    public required string Id { get; init; }

    /// <summary>通知标题。</summary>
    public string Title { get; init; } = default!;

    /// <summary>通知内容（可选）。</summary>
    public string? Content { get; init; }

    /// <summary>通知类型；默认 <see cref="NotificationInputDto.DefaultType"/>。</summary>
    public string Type { get; init; } = NotificationInputDto.DefaultType;

    /// <summary>点击跳转路由（可选）。</summary>
    public string? Link { get; init; }

    /// <summary>图标标识（可选，由前端解释）。</summary>
    public string? Icon { get; init; }

    /// <summary>是否已读。</summary>
    public bool IsRead { get; init; }

    /// <summary>创建时间（UTC）。</summary>
    /// <remarks>必填：与 <see cref="Id"/> 同一处定案，持久化层不生成也不替换。</remarks>
    public required DateTime CreationTime { get; init; }

    /// <summary>关联实体 ID（可选）。</summary>
    public string? RelatedEntityId { get; init; }

    /// <summary>关联实体类型（可选）。</summary>
    public string? RelatedEntityType { get; init; }

    /// <summary>扩展元数据（可选）。</summary>
    public Dictionary<string, object>? Metadata { get; init; }
}
