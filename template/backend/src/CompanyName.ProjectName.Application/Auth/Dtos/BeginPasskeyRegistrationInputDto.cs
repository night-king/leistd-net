using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CompanyName.ProjectName.Application.Auth.Policies;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>开始登记通行密钥：会话不够新时带上本人的一种凭证。</summary>
public sealed record BeginPasskeyRegistrationInputDto : IValidatableObject
{
    /// <summary>当前口令；会话刚认证过时不用带。</summary>
    [Display(Name = "Password")]
    [StringLength(256, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? Password { get; init; }

    /// <summary>身份验证器上的 6 位验证码，与当前口令二选一；会话刚认证过时都不用带。</summary>
    [Display(Name = "Verification code")]
    [RegularExpression(@"^\s*\d{3}\s?\d{3}\s*$", ErrorMessage = "The verification code must contain exactly 6 digits.")]
    public string? TwoFactorCode { get; init; }

    /// <summary>通行密钥断言的 JSON 形式（先经 <c>me/reauthentication/passkey-options</c> 取选项），与其他凭证三选一；会话刚认证过时不用带。</summary>
    public JsonElement? Passkey { get; init; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        ReauthenticationProof.ValidateOneAtATime(Password, TwoFactorCode, Passkey, nameof(TwoFactorCode));
}
