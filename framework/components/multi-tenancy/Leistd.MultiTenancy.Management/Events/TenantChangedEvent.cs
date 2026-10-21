using Leistd.EventBus.Events;

namespace Leistd.MultiTenancy.Management.Events;

/// <summary>租户经管理用例被创建、更新、启停或删除。</summary>
/// <remarks>
/// 在各步写入都已提交后发布；创建失败并已补偿时不发布。
/// </remarks>
/// <param name="tenantId">租户标识。</param>
/// <param name="displayName">显示名快照（没有显示名时为名称）；删除时取删除前的值，取不到为 <see langword="null"/>。</param>
/// <param name="change">变更类别。</param>
public sealed class TenantChangedEvent(Guid tenantId, string? displayName, TenantChangeKind change) : LocalEvent
{
    /// <summary>租户标识。</summary>
    public Guid TenantId { get; } = tenantId;

    /// <summary>显示名快照。</summary>
    public string? DisplayName { get; } = displayName;

    /// <summary>变更类别。</summary>
    public TenantChangeKind Change { get; } = change;
}

/// <summary>租户变更类别。</summary>
public enum TenantChangeKind
{
    /// <summary>创建并完成开通。</summary>
    Created,

    /// <summary>名称、显示名或描述被更新。</summary>
    Updated,

    /// <summary>启用或停用。</summary>
    ActivationChanged,

    /// <summary>软删除。</summary>
    Deleted
}
