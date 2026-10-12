using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Roles.Dtos;

/// <summary>创建角色输入 DTO。</summary>
public record CreateRoleInputDto
{
    [Display(Name = "Role name")]
    [Required(ErrorMessage = "{0} is required.")]
    [StringLength(64, MinimumLength = 2, ErrorMessage = "{0} must be between {2} and {1} characters.")]
    [RegularExpression(@"^[a-zA-Z0-9_]+$", ErrorMessage = "{0} can contain only letters, numbers, and underscores.")]
    public string Name { get; init; } = null!;

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
