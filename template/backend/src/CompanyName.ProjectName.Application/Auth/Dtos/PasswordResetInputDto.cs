using System.ComponentModel.DataAnnotations;
using CompanyName.ProjectName.Domain.Users.Policies;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>找回口令的第二步：发到邮箱的验证码与新口令。</summary>
public sealed record PasswordResetInputDto
{
    [Display(Name = "Email")]
    [Required(ErrorMessage = "{0} is required.")]
    [EmailAddress(ErrorMessage = "{0} has an invalid format.")]
    public string Email { get; init; } = null!;

    [Display(Name = "Email verification challenge")]
    [Required(ErrorMessage = "{0} is required.")]
    public Guid? ChallengeId { get; init; }

    [Display(Name = "Email verification code")]
    [Required(ErrorMessage = "{0} is required.")]
    [RegularExpression(@"^\s*\d{6}\s*$", ErrorMessage = "The email verification code must contain exactly 6 digits.")]
    public string Code { get; init; } = null!;

    /// <remarks>长度在这里先拦：校验码一旦通过就作废，口令不合规时不该白白用掉它。权威仍在服务端 PasswordPolicy。</remarks>
    [Display(Name = "New password")]
    [Required(ErrorMessage = "{0} is required.")]
    [StringLength(PasswordPolicy.MaximumLength, MinimumLength = PasswordPolicy.MinimumLength,
        ErrorMessage = "{0} must be between {2} and {1} characters.")]
    public string NewPassword { get; init; } = null!;
}
