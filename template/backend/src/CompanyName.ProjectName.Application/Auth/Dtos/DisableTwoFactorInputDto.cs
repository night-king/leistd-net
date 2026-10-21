using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>停用两步验证：有口令的账号口令与验证码都要；没有口令的账号只要验证码。</summary>
/// <remarks>口令是否必填由服务端按账号有没有口令判定，入参层不能知道。</remarks>
public sealed record DisableTwoFactorInputDto
{
    /// <summary>当前密码；没有本地口令的账号不带。</summary>
    [Display(Name = "Password")]
    [StringLength(256, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? Password { get; init; }

    /// <summary>身份验证器应用上的 6 位验证码。</summary>
    [Display(Name = "Verification code")]
    [Required(ErrorMessage = "{0} is required.")]
    [RegularExpression(@"^\s*\d{3}\s?\d{3}\s*$", ErrorMessage = "The verification code must contain exactly 6 digits.")]
    public string Code { get; init; } = null!;
}
