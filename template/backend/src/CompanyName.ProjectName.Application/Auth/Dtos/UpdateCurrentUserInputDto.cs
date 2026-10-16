using System.ComponentModel.DataAnnotations;
using CompanyName.ProjectName.Domain.Users.Constants;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

public record UpdateCurrentUserInputDto
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

    [Display(Name = "Phone number")]
    [MaxLength(20, ErrorMessage = "{0} cannot exceed {1} characters.")]
    [RegularExpression(@"^[0-9+\-()\s]{0,20}$", ErrorMessage = "{0} has an invalid format.")]
    public string? PhoneNumber { get; init; }

    /// <summary>本人的当前口令：换邮箱而会话不够新时带上，其余修改不用。</summary>
    [Display(Name = "Current password")]
    [StringLength(256, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? CurrentPassword { get; init; }
}
