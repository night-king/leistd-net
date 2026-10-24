#if (LocalIdentity)
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CompanyName.ProjectName.Domain.Users.Policies;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>给没有本地口令的账号（只经外部登录建立）设置第一个口令。</summary>
/// <remarks>账号本来没有口令，确认身份只能靠会话刚认证过、身份验证器上的验证码或通行密钥。</remarks>
public record SetPasswordInputDto
{
    [Display(Name = "New password")]
    [Required(ErrorMessage = "{0} is required.")]
    // 仅快速反馈；权威在服务端 PasswordPolicy
    [StringLength(PasswordPolicy.MaximumLength, MinimumLength = PasswordPolicy.MinimumLength,
        ErrorMessage = "{0} must be between {2} and {1} characters.")]
    public string NewPassword { get; init; } = null!;

    [Display(Name = "Confirm password")]
    [Required(ErrorMessage = "{0} is required.")]
    public string ConfirmPassword { get; init; } = null!;

    /// <summary>本人身份验证器上的 6 位验证码；会话不够新时才需要。</summary>
    [Display(Name = "Verification code")]
    [RegularExpression(@"^\s*\d{3}\s?\d{3}\s*$", ErrorMessage = "The verification code must contain exactly 6 digits.")]
    public string? TwoFactorCode { get; init; }

    /// <summary>通行密钥断言的 JSON 形式，与验证码二选一；会话不够新时才需要。</summary>
    public JsonElement? Passkey { get; init; }
}
#endif
