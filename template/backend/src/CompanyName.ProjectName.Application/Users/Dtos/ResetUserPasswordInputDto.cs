#if (LocalIdentity)
using System.ComponentModel.DataAnnotations;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Domain.Users.Policies;

namespace CompanyName.ProjectName.Application.Users.Dtos;

/// <summary>重置用户密码：<see cref="Password"/> 是给对方设的新口令；管理员的会话不够新时另带自己的当前口令。</summary>
public record ResetUserPasswordInputDto : IValidatableObject
{
    [Display(Name = "Password")]
    [Required(ErrorMessage = "{0} is required.")]
    // 仅快速反馈；权威在服务端 PasswordPolicy
    [StringLength(PasswordPolicy.MaximumLength, MinimumLength = PasswordPolicy.MinimumLength,
        ErrorMessage = "{0} must be between {2} and {1} characters.")]
    public string Password { get; init; } = null!;

    /// <summary>管理员本人的当前口令；会话刚认证过时不用带。不是被重置用户的口令。</summary>
    [Display(Name = "Current password")]
    [StringLength(256, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? CurrentPassword { get; init; }

    /// <summary>管理员本人身份验证器上的 6 位验证码，与管理员本人的当前口令二选一；会话刚认证过时都不用带。</summary>
    [Display(Name = "Verification code")]
    [RegularExpression(@"^\s*\d{3}\s?\d{3}\s*$", ErrorMessage = "The verification code must contain exactly 6 digits.")]
    public string? TwoFactorCode { get; init; }

    /// <inheritdoc />
    /// <remarks>一次只出示一种凭证：两种都给时不猜用哪个，按字段错误拒绝。</remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        ReauthenticationProof.ValidateOneAtATime(CurrentPassword, TwoFactorCode, nameof(TwoFactorCode));
}
#endif
