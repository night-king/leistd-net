using System.ComponentModel.DataAnnotations;
using CompanyName.ProjectName.Domain.Users.Policies;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

public record ChangePasswordInputDto
{
    [Display(Name = "Current password")]
    [Required(ErrorMessage = "{0} is required.")]
    public string CurrentPassword { get; init; } = null!;

    [Display(Name = "New password")]
    [Required(ErrorMessage = "{0} is required.")]
    // 仅快速反馈；权威在服务端 PasswordPolicy
    [StringLength(PasswordPolicy.MaximumLength, MinimumLength = PasswordPolicy.MinimumLength,
        ErrorMessage = "{0} must be between {2} and {1} characters.")]
    public string NewPassword { get; init; } = null!;

    [Display(Name = "Confirm password")]
    [Required(ErrorMessage = "{0} is required.")]
    public string ConfirmPassword { get; init; } = null!;
}
