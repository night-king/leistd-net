using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>提交一个验证码（启用、重新生成恢复码时用来证明手上确有身份验证器）。</summary>
public sealed record TwoFactorCodeInputDto
{
    /// <summary>身份验证器应用上的 6 位验证码。</summary>
    [Display(Name = "Verification code")]
    [Required(ErrorMessage = "{0} is required.")]
    [RegularExpression(@"^\s*\d{3}\s?\d{3}\s*$", ErrorMessage = "The verification code must contain exactly 6 digits.")]
    public string Code { get; init; } = null!;
}
