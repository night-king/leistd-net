using System.ComponentModel.DataAnnotations;
using CompanyName.ProjectName.Domain.Users.Constants;
using CompanyName.ProjectName.Domain.Users.Policies;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>注册请求 DTO。</summary>
public record RegisterInputDto
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

    [Display(Name = "Password")]
    [Required(ErrorMessage = "{0} is required.")]
    // 只做快速反馈，安全不变量在服务端 PasswordPolicy（下限必须与它一致，否则前置校验形同虚设）
    [StringLength(PasswordPolicy.MaximumLength, MinimumLength = PasswordPolicy.MinimumLength,
        ErrorMessage = "{0} must be between {2} and {1} characters.")]
    public string Password { get; init; } = null!;

    [Display(Name = "Display name")]
    [StringLength(128, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? DisplayName { get; init; }

    [Display(Name = "Captcha token")]
    public string? CaptchaToken { get; init; }

    [Display(Name = "Captcha")]
    [StringLength(10, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? CaptchaCode { get; init; }
#if (Email)

    [Display(Name = "Email verification")]
    public EmailVerificationInputDto? EmailVerification { get; init; }
#endif
}
