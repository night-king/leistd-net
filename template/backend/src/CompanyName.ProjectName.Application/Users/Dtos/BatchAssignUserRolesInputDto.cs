using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Users.Dtos;

/// <summary>批量为用户追加角色输入 DTO。</summary>
/// <remarks>只追加不覆盖：用户已有的角色原样保留，要撤销角色走单个用户的角色替换。</remarks>
public record BatchAssignUserRolesInputDto : BatchUsersInputDto
{
    /// <summary>要追加的角色 Id。</summary>
    [Display(Name = "Roles")]
    [Required(ErrorMessage = "{0} is required.")]
    [MinLength(1, ErrorMessage = "{0} is required.")]
    [MaxLength(20, ErrorMessage = "{0} cannot contain more than {1} items.")]
    public List<Guid> RoleIds { get; init; } = null!;
}
