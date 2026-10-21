#if (RemoteTokenAuth)
namespace CompanyName.ProjectName.Application.Users.Dtos;

/// <summary>资源服务的当前主体：身份资料取自签发方令牌，超管标记与角色取本服务的授权数据。</summary>
public sealed record CurrentResourceUserOutputDto
{
    /// <summary>用户 Id（令牌的主体标识）；非用户主体为 null。</summary>
    public Guid? Id { get; init; }

    /// <summary>用户名；令牌没有时为空串。</summary>
    public required string Username { get; init; }

    /// <summary>邮箱；令牌没有时为空串。</summary>
    public required string Email { get; init; }

    /// <summary>显示名。</summary>
    public string? DisplayName { get; init; }

    /// <summary>签发方是否已验证该邮箱。</summary>
    public bool IsEmailVerified { get; init; }

    /// <summary>本服务授予的超级管理员标记（签发方令牌里的同名声明不算）。</summary>
    public bool IsSuperAdmin { get; init; }

    /// <summary>本服务授予的角色名。</summary>
    public required IReadOnlyList<string> Roles { get; init; }

    /// <summary>当前租户；宿主为 null。</summary>
    public Guid? TenantId { get; init; }
}
#endif
