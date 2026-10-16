namespace Leistd.Settings.AspNetCore.Endpoints;

/// <summary>设置端点的授权口径。</summary>
/// <remarks>
/// 两个策略名都必填，组件不内置默认策略，也不在路由组上叠加宿主默认策略；漏配任一项时 <c>MapSettings</c> 映射即抛出。
/// <see cref="GroupPolicies"/> 可选，用于把不同分组的管理权交给不同的人。
/// </remarks>
public sealed class SettingEndpointOptions
{
    /// <summary>读取设置与写自己偏好所需的授权策略名，通常是“已登录的交互用户”。</summary>
    public string AccessPolicy { get; set; } = string.Empty;

    /// <summary>写入当前租户设置所需的授权策略名。</summary>
    public string TenantWritePolicy { get; set; } = string.Empty;

    /// <summary>分组标识 → 管理该分组还需满足的授权策略名。</summary>
    /// <remarks>
    /// <para>登记了策略的分组，写入租户层（宿主上下文下为进程级）要同时满足 <see cref="TenantWritePolicy"/> 与该策略；
    /// 未登记的分组只要求 <see cref="TenantWritePolicy"/>。分组策略不满足时按该策略的认证方案质询（401）或拒绝（403）。</para>
    /// <para>读取按同一判据给出 <c>CanManage</c>：不能管理、又不允许用户覆盖的设置不下发，管理值只给管理者。</para>
    /// </remarks>
    public IDictionary<string, string> GroupPolicies { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    // 缺必填项抛 ArgumentException，ParamName 即属性名
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(AccessPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(TenantWritePolicy);
        foreach (var (group, policy) in GroupPolicies)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(group, nameof(GroupPolicies));
            ArgumentException.ThrowIfNullOrWhiteSpace(policy, nameof(GroupPolicies));
        }
    }
}
