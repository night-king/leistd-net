using Leistd.EventBus.Events;

namespace Leistd.MultiTenancy.Management.Events;

/// <summary>某个租户的一条命名连接被登记、改写或删除。</summary>
/// <remarks>
/// <para>不含连接串：事件会进入日志、审计与订阅者的存储。</para>
/// <para>多租户组件不依赖操作记录组件，宿主订阅本事件后自行留痕。</para>
/// </remarks>
/// <param name="tenantId">租户标识。</param>
/// <param name="tenantDisplayName">租户显示名快照（没有显示名时为名称）；取不到为 <see langword="null"/>。</param>
/// <param name="name">连接名（已归一化）。</param>
/// <param name="change">发生了哪种变化。</param>
/// <param name="version">变化之后该行的版本；删除时是被删掉的那个版本。</param>
public sealed class TenantConnectionChangedEvent(
    Guid tenantId,
    string? tenantDisplayName,
    string name,
    TenantConnectionChangeKind change,
    long version) : LocalEvent
{
    /// <summary>租户标识。</summary>
    public Guid TenantId { get; } = tenantId;

    /// <summary>
    /// 租户显示名快照，与 <see cref="TenantChangedEvent.DisplayName"/> 同一口径；记录“为哪个租户改的”时用它，
    /// 不要用连接名 <see cref="Name"/> 顶替。
    /// </summary>
    public string? TenantDisplayName { get; } = tenantDisplayName;

    /// <summary>连接名（已归一化），例如 <c>default</c>、<c>crm</c>。不是租户名。</summary>
    public string Name { get; } = name;

    /// <summary>变化类型。</summary>
    public TenantConnectionChangeKind Change { get; } = change;

    /// <summary>变化后的版本；删除时是被删掉的版本。</summary>
    public long Version { get; } = version;
}

/// <summary>连接登记的变化类型。</summary>
public enum TenantConnectionChangeKind
{
    /// <summary>首次登记。</summary>
    Registered,

    /// <summary>改写已有登记（换库或轮换凭据）。</summary>
    Changed,

    /// <summary>删除登记，该名字回落到服务自己的配置。</summary>
    Removed
}
