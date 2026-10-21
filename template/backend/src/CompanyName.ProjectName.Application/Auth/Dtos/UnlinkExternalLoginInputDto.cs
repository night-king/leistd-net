#if (LocalIdentity)
using System.ComponentModel.DataAnnotations;
using CompanyName.ProjectName.Application.Auth.Policies;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>解绑外部账号：会话不够新时带上本人的当前口令或验证码（二选一）。</summary>
public record UnlinkExternalLoginInputDto : IValidatableObject
{
    /// <summary>本人的当前口令；会话刚认证过时不用带。</summary>
    [Display(Name = "Password")]
    [StringLength(256, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? Password { get; init; }

    /// <summary>本人身份验证器上的 6 位验证码，与当前口令二选一；会话刚认证过时都不用带。</summary>
    [Display(Name = "Verification code")]
    [RegularExpression(@"^\s*\d{3}\s?\d{3}\s*$", ErrorMessage = "The verification code must contain exactly 6 digits.")]
    public string? TwoFactorCode { get; init; }

    /// <inheritdoc />
    /// <remarks>一次只出示一种凭证：两种都给时不猜用哪个，按字段错误拒绝。</remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        ReauthenticationProof.ValidateOneAtATime(Password, TwoFactorCode, nameof(TwoFactorCode));
}
#endif
