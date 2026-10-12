using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>停用两步验证：密码与验证码都要。</summary>
public sealed record DisableTwoFactorInputDto
{
    /// <summary>当前密码。</summary>
    [Display(Name = "Password")]
    [Required(ErrorMessage = "{0} is required.")]
    [StringLength(256, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string Password { get; init; } = null!;

    /// <summary>身份验证器应用上的 6 位验证码。</summary>
    [Display(Name = "Verification code")]
    [Required(ErrorMessage = "{0} is required.")]
    [RegularExpression(@"^\s*\d{3}\s?\d{3}\s*$", ErrorMessage = "The verification code must contain exactly 6 digits.")]
    public string Code { get; init; } = null!;
}
