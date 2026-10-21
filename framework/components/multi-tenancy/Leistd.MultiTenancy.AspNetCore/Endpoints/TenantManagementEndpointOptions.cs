namespace Leistd.MultiTenancy.AspNetCore.Endpoints;

/// <summary>租户管理端点的授权口径，全部必填。</summary>
/// <remarks>组件不内置默认策略，漏配任何一项时映射即抛出。</remarks>
public sealed class TenantManagementEndpointOptions
{
    /// <summary>查询租户列表与详情所需的策略名。</summary>
    public string ReadPolicy { get; set; } = string.Empty;

    /// <summary>创建租户所需的策略名。</summary>
    public string CreatePolicy { get; set; } = string.Empty;

    /// <summary>更新与启停租户所需的策略名。</summary>
    public string UpdatePolicy { get; set; } = string.Empty;

    /// <summary>删除租户所需的策略名。</summary>
    public string DeletePolicy { get; set; } = string.Empty;

    // 缺必填项抛 ArgumentException，ParamName 即属性名
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ReadPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(CreatePolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(UpdatePolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(DeletePolicy);
    }
}

/// <summary>租户连接端点的授权口径。</summary>
/// <remarks>
/// <see cref="ManagePolicy"/> 必填。两个机器端点各自的策略为空时不映射该端点，不留无人可用的内部端点。
/// </remarks>
public sealed class TenantConnectionEndpointOptions
{
    /// <summary>查看与维护连接登记所需的策略名。</summary>
    public string ManagePolicy { get; set; } = string.Empty;

    /// <summary>资源服务读取运行时连接（<c>GET runtime/{tenantId}</c>）所需的策略名；为空不映射。</summary>
    public string? RuntimeReadPolicy { get; set; }

    /// <summary>迁移作业枚举迁移目标（<c>GET migration</c>）所需的策略名；为空不映射。</summary>
    public string? MigrationReadPolicy { get; set; }

    // 两个机器端点的策略为空是"不映射"，只有 ManagePolicy 必填
    internal void Validate() => ArgumentException.ThrowIfNullOrWhiteSpace(ManagePolicy);
}
