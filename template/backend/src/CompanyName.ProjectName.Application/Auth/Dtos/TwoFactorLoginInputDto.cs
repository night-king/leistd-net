using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>登录第二步：提交验证码或恢复码（二选一）。</summary>
public sealed record TwoFactorLoginInputDto : IValidatableObject
{
    /// <summary>第一步返回的凭据。</summary>
    [Display(Name = "Two-factor token")]
    [Required(ErrorMessage = "{0} is required.")]
    [StringLength(128, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string Token { get; init; } = null!;

    /// <summary>身份验证器应用上的 6 位验证码。</summary>
    [Display(Name = "Verification code")]
    [RegularExpression(@"^\s*\d{3}\s?\d{3}\s*$", ErrorMessage = "The verification code must contain exactly 6 digits.")]
    public string? Code { get; init; }

    /// <summary>恢复码（手机不在身边时用）。</summary>
    [Display(Name = "Recovery code")]
    [StringLength(64, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? RecoveryCode { get; init; }

    /// <inheritdoc />
    /// <remarks>二选一没有对应的内置特性；文案与特性文案一样是本地化键。</remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Code) && string.IsNullOrWhiteSpace(RecoveryCode))
            yield return new ValidationResult("Enter the verification code or a recovery code.", [nameof(Code)]);
    }
}
