using System.ComponentModel.DataAnnotations;
using CompanyName.ProjectName.Domain.Users.Constants;
using CompanyName.ProjectName.Domain.Users.Policies;

namespace CompanyName.ProjectName.Application.Users.Dtos;

/// <summary>创建用户输入 DTO。</summary>
public record CreateUserInputDto
{
    [Display(Name = "Username")]
    [Required(ErrorMessage = "{0} is required.")]
    [StringLength(UsernameRules.MaxLength, MinimumLength = UsernameRules.MinLength, ErrorMessage = "{0} must be between {2} and {1} characters.")]
    [RegularExpression(UsernameRules.Pattern, ErrorMessage = "{0} can contain only letters, numbers, and underscores.")]
    public string Username { get; init; } = null!;

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

    [Display(Name = "Password")]
    [Required(ErrorMessage = "{0} is required.")]
    // 仅快速反馈；权威在服务端 PasswordPolicy
    [StringLength(PasswordPolicy.MaximumLength, MinimumLength = PasswordPolicy.MinimumLength,
        ErrorMessage = "{0} must be between {2} and {1} characters.")]
    public string Password { get; init; } = null!;

    public bool IsActive { get; init; } = true;

    public bool IsEmailVerified { get; init; }

    /// <summary>初始角色 Id 集合。按 Id 提交而非角色名，角色名只用于展示。</summary>
    /// <remarks>
    /// 非空时应用服务会额外要求 <c>App.Users.ManageRoles</c>：
    /// 只持有创建权限的主体不能在创建时把自己或他人提升为管理员。
    /// </remarks>
    [Display(Name = "Roles")]
    [MaxLength(20, ErrorMessage = "{0} cannot contain more than {1} items.")]
    public List<Guid> RoleIds { get; init; } = [];
}
