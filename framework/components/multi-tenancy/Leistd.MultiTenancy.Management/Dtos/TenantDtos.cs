using System.ComponentModel.DataAnnotations;
using Leistd.Data.Paging;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Stores;

namespace Leistd.MultiTenancy.Management.Dtos;

/// <summary>租户分页查询入参。</summary>
public record GetTenantPagedInputDto : PageRequest
{
    /// <summary>名称或显示名关键字（大小写不敏感）。</summary>
    public string? Keyword { get; init; }
}

/// <summary>租户输出。</summary>
public record TenantOutputDto
{
    /// <summary>租户标识。</summary>
    public required Guid Id { get; init; }

    /// <summary>大小写不敏感的唯一名称。</summary>
    public required string Name { get; init; }

    /// <summary>显示名。</summary>
    public string? DisplayName { get; init; }

    /// <summary>简短描述。</summary>
    public string? Description { get; init; }

    /// <summary>是否启用。</summary>
    public required bool IsActive { get; init; }

    /// <summary>创建时间。</summary>
    public required DateTime CreationTime { get; init; }
}

/// <summary>域名对租户的定案结果。</summary>
/// <remarks>
/// <see cref="Host"/> 是域名已定案为宿主，<see cref="Undecided"/> 是域名不表态、后续解析来源仍可决定；
/// 客户端须区分两者，否则会在宿主域上沿用上次记住的租户。
/// </remarks>
public enum HostTenantDecision
{
    /// <summary>主机名不在受管域内，或未配置域名格式：域名不参与定案。</summary>
    Undecided,

    /// <summary>受管域内但未指向任何租户：定案为宿主。</summary>
    Host,

    /// <summary>受管域内的租户子域：定案为该租户。</summary>
    Tenant
}

/// <summary>按主机名解析租户的结果。</summary>
public record TenantByHostOutputDto
{
    /// <summary>定案结果。</summary>
    public required HostTenantDecision Decision { get; init; }

    /// <summary>定案到的租户，只有 <see cref="HostTenantDecision.Tenant"/> 时才可能有值。</summary>
    public AnonymousTenantOutputDto? Tenant { get; init; }
}

/// <summary>匿名响应里的租户：只有名字。</summary>
/// <remarks>
/// 不含标识与启用状态；存在性仍可被匿名判定，是有意接受的边界（见组件文档）。
/// 客户端把名字随后续请求发出即可，租户头按名字同样能解析。
/// </remarks>
public sealed record AnonymousTenantOutputDto
{
    /// <summary>租户名。</summary>
    public required string Name { get; init; }
}

/// <summary>创建租户入参。宿主要在开通时收集更多信息（如租户管理员），就派生这个类型并交给端点与开通器。</summary>
public record CreateTenantInputDto
{
    /// <summary>租户名称，须匹配 <see cref="TenantConfiguration.NamePattern"/>。</summary>
    [Required]
    [MaxLength(TenantConfiguration.MaxNameLength)]
    public string Name { get; init; } = null!;

    /// <summary>显示名。</summary>
    [MaxLength(128)]
    public string? DisplayName { get; init; }

    /// <summary>简短描述。</summary>
    [MaxLength(256)]
    public string? Description { get; init; }

    /// <summary>该租户的专属库连接，按名字登记；留空即不分库，各服务用自己配置的库。</summary>
    /// <remarks>
    /// <para>库要先建好并迁移过，这里只登记；开通紧随登记之后，解析到的已经是这些库。</para>
    /// <para>多服务部署可一次登记多条（如 <c>default</c>、<c>crm</c>），租户与全部连接在同一个控制面工作单元里落库。
    /// 请求体显式传 <see langword="null"/> 时按空列表处理。</para>
    /// </remarks>
    public IReadOnlyList<CreateTenantConnectionInputDto> Connections
    {
        get => _connections;
        init => _connections = value ?? [];
    }

    private readonly IReadOnlyList<CreateTenantConnectionInputDto> _connections = [];

    /// <summary>不输出连接串。</summary>
    public override string ToString() =>
        $"{nameof(CreateTenantInputDto)} {{ Name = {Name}, DisplayName = {DisplayName}, Connections = {Connections.Count} }}";
}

/// <summary>创建租户时登记的一条命名连接。</summary>
/// <remarks>名字与连接串在任何库操作之前统一校验：名字按 <c>^[a-z0-9-]{1,64}$</c> 归一化，重名与语法错误都在写库前拒绝。</remarks>
public record CreateTenantConnectionInputDto
{
    /// <summary>连接名，对应使用方 DbContext 的 <c>[ConnectionStringName]</c>；不区分大小写。</summary>
    [Required]
    [MaxLength(TenantConnectionConfiguration.MaxNameLength)]
    public string Name { get; init; } = null!;

    /// <summary>连接串。</summary>
    [Required]
    [MaxLength(TenantConnectionConfiguration.MaxConnectionStringLength)]
    public string ConnectionString { get; init; } = null!;

    /// <summary>不输出连接串。</summary>
    public override string ToString() => $"{nameof(CreateTenantConnectionInputDto)} {{ Name = {Name} }}";
}

/// <summary>更新租户入参；三个字段都按传入值覆盖。</summary>
public record UpdateTenantInputDto
{
    /// <summary>
    /// 租户名称。与原名不同时须匹配 <see cref="TenantConfiguration.NamePattern"/>；
    /// 原名不变时原样接受，长度上限为 <see cref="TenantConfiguration.MaxStoredNameLength"/>。
    /// </summary>
    [Required]
    [MaxLength(TenantConfiguration.MaxStoredNameLength)]
    public string Name { get; init; } = null!;

    /// <summary>显示名；传 <see langword="null"/> 即清空。</summary>
    [MaxLength(128)]
    public string? DisplayName { get; init; }

    /// <summary>简短描述；传 <see langword="null"/> 即清空。</summary>
    [MaxLength(256)]
    public string? Description { get; init; }
}

/// <summary>租户启停入参。</summary>
public record UpdateTenantActivationInputDto
{
    /// <summary>目标状态；停用后该租户的请求自下一次校验起被拒绝。</summary>
    /// <remarks>可空只为让请求体漏掉该字段时按字段报验证错误，而不是静默当成停用；用例入口验证后才取值。</remarks>
    [Required]
    public bool? IsActive { get; init; }
}
