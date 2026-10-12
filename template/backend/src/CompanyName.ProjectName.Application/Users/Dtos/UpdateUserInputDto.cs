using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Users.Dtos;

/// <summary>更新用户输入 DTO。</summary>
/// <remarks>
/// 不包含角色字段：角色分配是独立的命令，走 <c>PUT /api/v1/users/{id}/roles</c>
/// 并要求 <c>App.Users.ManageRoles</c>。否则任何持有用户编辑权限的主体都能提权。
/// </remarks>
/// <remarks>
/// 不含启用状态：改变账号可用性只有启用/禁用两个命令一个入口。放在普通更新里就成了第二个入口，
/// 而保护规则（超级管理员不得禁用自己）只写在专用命令上——两个入口迟早漂移，
/// 事实上超管当时就能经由这里把自己禁掉。
/// </remarks>
public record UpdateUserInputDto
{
    [Display(Name = "Email")]
    [Required(ErrorMessage = "{0} is required.")]
    [EmailAddress(ErrorMessage = "{0} has an invalid format.")]
    [StringLength(256, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string Email { get; init; } = null!;

    [Display(Name = "Display name")]
    [StringLength(128, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? DisplayName { get; init; }

    [Display(Name = "Avatar")]
    [StringLength(1500000, ErrorMessage = "{0} is too large. Compress it and try again.")]
    public string? Avatar { get; init; }

    public bool IsEmailVerified { get; init; }
}
