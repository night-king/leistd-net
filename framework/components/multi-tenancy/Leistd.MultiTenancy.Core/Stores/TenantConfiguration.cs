namespace Leistd.MultiTenancy.Stores;

/// <summary>表示与存储实现无关的租户配置快照。</summary>
public class TenantConfiguration
{
    /// <summary>租户名的最大长度，即单个 DNS 标签的上限。</summary>
    public const int MaxNameLength = 63;

    /// <summary>租户名的存储容量；名称不变的更新按此容量原样保存，新名称上限由 <see cref="MaxNameLength"/> 约束。</summary>
    public const int MaxStoredNameLength = 64;

    /// <summary>租户名的合法形态：单个 DNS 标签——字母、数字与连字符，不以连字符开头或结尾，至多 63 个字符。</summary>
    /// <remarks>
    /// 按子域名解析租户（<c>MultiTenancyOptions.DomainFormat</c>）时租户名就是主机名的一段。
    /// 大小写不限：名字大小写不敏感唯一。
    /// </remarks>
    public const string NamePattern = "^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$";

    /// <summary>租户标识。</summary>
    public required Guid Id { get; init; }

    /// <summary>大小写不敏感的唯一租户名称。</summary>
    public required string Name { get; init; }

    /// <summary>由 <see cref="ITenantNormalizer"/> 生成的查询键。</summary>
    public required string NormalizedName { get; init; }

    /// <summary>显示名称。</summary>
    public string? DisplayName { get; init; }

    /// <summary>简短描述。</summary>
    public string? Description { get; init; }

    /// <summary>租户是否启用。</summary>
    public bool IsActive { get; init; } = true;

    /// <summary>创建时间。</summary>
    public DateTime CreationTime { get; init; }
}
