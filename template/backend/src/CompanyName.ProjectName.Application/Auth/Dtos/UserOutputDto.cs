namespace CompanyName.ProjectName.Application.Auth.Dtos;

public record UserOutputDto
{
    public required Guid Id { get; init; }
    public required string Username { get; init; }
    public required string Email { get; init; }
    public string? DisplayName { get; init; }
    /// <summary>头像地址：外部地址原样给出，上传的图片给带版本号的站内地址（见 AvatarUrls）。</summary>
    public string? Avatar { get; init; }
    public string? PhoneNumber { get; init; }
    public bool IsActive { get; init; }
    /// <summary>当前邮箱是否已验证；改过邮箱后回到未验证。</summary>
    public bool IsEmailVerified { get; init; }

    /// <summary>是否已启用两步验证。</summary>
    public bool IsTwoFactorEnabled { get; init; }

    /// <summary>是否设有本地口令；只经外部登录建立的账号没有。</summary>
    /// <remarks>与 <see cref="IsTwoFactorEnabled"/> 一起决定确认身份时能出示哪种凭证，是否需要确认仍由服务端判定。</remarks>
    public bool HasPassword { get; init; }

    /// <summary>当前是受限会话：所在租户要求两步验证而本人尚未启用，必须先完成设置。</summary>
    /// <remarks>
    /// 来自本次请求的会话校验（按当时的策略与本人的两步验证状态），与服务端拦截同一判定，
    /// 只在"获取当前用户"时有值。策略或状态一变，下一次获取即反映，不必重新登录。
    /// </remarks>
    public bool TwoFactorSetupRequired { get; init; }
    public bool IsSuperAdmin { get; init; }
    public DateTime CreationTime { get; init; }
    /// <summary>已分配角色名，仅用于展示；权限判断一律走 /api/v1/permissions/current。</summary>
    public required string[] Roles { get; init; }
}
