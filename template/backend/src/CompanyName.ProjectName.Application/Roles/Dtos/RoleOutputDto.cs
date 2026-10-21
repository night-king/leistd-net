namespace CompanyName.ProjectName.Application.Roles.Dtos;

/// <summary>角色输出 DTO。</summary>
public record RoleOutputDto
{
    public required Guid Id { get; init; }

    /// <summary>角色名称，唯一且创建后不可修改，作为稳定的业务标识。</summary>
    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    /// <summary>系统内置角色，不可删除。</summary>
    public bool IsStatic { get; init; }

    /// <summary>默认角色，新用户自动分配。</summary>
    public bool IsDefault { get; init; }

    public int Sort { get; init; }

    /// <summary>关联用户数。</summary>
    public int UserCount { get; init; }

    /// <summary>已授予的权限数。</summary>
    public int PermissionCount { get; init; }

    public DateTime CreationTime { get; init; }

    public DateTime? LastModificationTime { get; init; }
}
