using CompanyName.ProjectName.Application.Roles.Dtos;

namespace CompanyName.ProjectName.Application.Users.Dtos;

/// <summary>用户管理输出 DTO。</summary>
public record UserManagementOutputDto
{
    public required Guid Id { get; init; }
    public required string Username { get; init; }
    public required string Email { get; init; }
    public string? DisplayName { get; init; }
    public string? Avatar { get; init; }
    public bool IsActive { get; init; }
#if (LocalIdentity)
    public bool IsEmailVerified { get; init; }
#endif
    /// <summary>已分配角色。携带 Id 供提交使用，Name 与 DisplayName 只用于展示。</summary>
    public required IReadOnlyList<RoleBriefOutputDto> Roles { get; init; }
    public bool IsSuperAdmin { get; init; }
    public DateTime CreationTime { get; init; }
#if (LocalIdentity)
    public DateTime? LastLoginTime { get; init; }

    /// <summary>登录锁定正在生效（登录失败触发的临时锁定，或管理员锁定）。</summary>
    public bool IsLockedOut { get; init; }

    /// <summary>锁定截止时间；未锁定或无期限（管理员锁定）时为 null。</summary>
    public DateTime? LockoutEnd { get; init; }

    /// <summary>是否已启用两步验证。</summary>
    public bool IsTwoFactorEnabled { get; init; }
#endif
}
