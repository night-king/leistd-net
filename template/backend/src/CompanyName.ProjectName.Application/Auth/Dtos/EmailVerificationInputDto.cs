using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>注册时提交的邮箱验证挑战应答。</summary>
public sealed record EmailVerificationInputDto
{
    [Display(Name = "Email verification challenge")]
    [Required(ErrorMessage = "{0} is required.")]
    public Guid? ChallengeId { get; init; }

    [Display(Name = "Email verification code")]
    [Required(ErrorMessage = "{0} is required.")]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "The email verification code must contain exactly 6 digits.")]
    public string Code { get; init; } = null!;
}
