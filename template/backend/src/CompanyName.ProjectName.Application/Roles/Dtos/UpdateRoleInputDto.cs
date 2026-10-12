using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Roles.Dtos;

/// <summary>更新角色输入 DTO。角色名称是稳定业务标识，创建后不可修改。</summary>
public record UpdateRoleInputDto
{
    [Display(Name = "Display name")]
    [Required(ErrorMessage = "{0} is required.")]
    [StringLength(128, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string DisplayName { get; init; } = null!;

    [Display(Name = "Description")]
    [StringLength(512, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? Description { get; init; }

    [Display(Name = "Sort")]
    [Range(0, 9999, ErrorMessage = "{0} must be between {1} and {2}.")]
    public int Sort { get; init; }

    public bool IsDefault { get; init; }
}
