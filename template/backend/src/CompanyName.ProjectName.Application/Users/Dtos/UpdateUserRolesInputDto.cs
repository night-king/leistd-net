using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Users.Dtos;

/// <summary>替换用户角色输入 DTO。</summary>
/// <remarks>
/// 角色分配与普通资料更新分属两个命令和两个权限：
/// 只有 <c>App.Users.ManageRoles</c> 能改角色，只有 <c>App.Users.Update</c> 能改资料。
/// </remarks>
public record UpdateUserRolesInputDto
{
    /// <summary>目标角色 Id 集合，未出现的角色视为解除关联。</summary>
    [Display(Name = "Roles")]
    [MaxLength(20, ErrorMessage = "{0} cannot contain more than {1} items.")]
    public List<Guid> RoleIds { get; init; } = [];
}
