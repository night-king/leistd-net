using System.ComponentModel.DataAnnotations;

namespace Leistd.Authorization.Dtos;

/// <summary>当前用户在当前侧别上的有效权限。</summary>
/// <remarks>前端据此裁剪路由、菜单和按钮；裁剪只影响体验，服务端仍对每个请求独立校验。</remarks>
public record CurrentPermissionsOutputDto
{
    /// <summary>已生效的权限名（用户直授与各角色授予的并集）；超级管理员为全部可用权限。</summary>
    public required IReadOnlyList<string> Permissions { get; init; }

    /// <summary>是否超级管理员（旁路全部功能权限）。</summary>
    public bool IsSuperAdmin { get; init; }

    /// <summary>有效权限的版本标记，变化即表示本地缓存已过期。</summary>
    public required string VersionToken { get; init; }
}

/// <summary>权限定义（树节点）。</summary>
public record PermissionDefinitionOutputDto
{
    /// <summary>权限名。</summary>
    public required string Name { get; init; }

    /// <summary>已按请求 culture 翻译的显示名。</summary>
    public required string DisplayName { get; init; }

    /// <summary>父权限名；顶层为 <see langword="null"/>。</summary>
    public string? ParentName { get; init; }

    /// <summary>当前侧别上可用的子权限。</summary>
    public required IReadOnlyList<PermissionDefinitionOutputDto> Children { get; init; }
}

/// <summary>权限定义分组。</summary>
public record PermissionDefinitionGroupOutputDto
{
    /// <summary>分组名。</summary>
    public required string Name { get; init; }

    /// <summary>已按请求 culture 翻译的显示名。</summary>
    public required string DisplayName { get; init; }

    /// <summary>当前侧别上可用的顶层权限。</summary>
    public required IReadOnlyList<PermissionDefinitionOutputDto> Permissions { get; init; }
}

/// <summary>单个权限在某主体上的授予状态。</summary>
/// <remarks>授予是纯加法，只有"已授予"与"未授予"两种。</remarks>
public record PermissionGrantStateDto
{
    /// <summary>权限名。</summary>
    public required string Name { get; init; }

    /// <summary>是否已授予。</summary>
    public bool Granted { get; init; }
}

/// <summary>某个主体的权限授予及其并发版本。</summary>
public record PermissionGrantsOutputDto
{
    /// <summary>授予对象类型。</summary>
    public required string ProviderName { get; init; }

    /// <summary>授予对象 Key。</summary>
    public required string ProviderKey { get; init; }

    /// <summary>乐观并发版本，保存时原样回传。</summary>
    public long Version { get; init; }

    /// <summary>当前侧别上每个可用权限的授予状态。</summary>
    public required IReadOnlyList<PermissionGrantStateDto> Grants { get; init; }
}

/// <summary>原子替换某个主体的全部授予。</summary>
public record ReplacePermissionGrantsInputDto
{
    /// <summary>单次替换的权限数上限。</summary>
    public const int MaximumPermissionCount = 500;

    /// <summary>期望的当前版本；与存储版本不一致时拒绝保存，避免两个管理员同时操作时后写覆盖前写。</summary>
    public long ExpectedVersion { get; init; }

    /// <summary>目标权限名集合，未出现的权限视为撤销。</summary>
    [MaxLength(MaximumPermissionCount)]
    public IReadOnlyList<string> PermissionNames { get; init; } = [];
}
