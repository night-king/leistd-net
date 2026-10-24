using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>重新生成恢复码：出示一个独立因子，身份验证器上的验证码或通行密钥断言，恰好一种。</summary>
/// <remarks>不认口令与恢复码：恢复码能绕过第二步，换一组新的要证明手上确有一个独立因子。</remarks>
public sealed record RegenerateRecoveryCodesInputDto : IValidatableObject
{
    /// <summary>身份验证器应用上的 6 位验证码。</summary>
    [Display(Name = "Verification code")]
    [RegularExpression(@"^\s*\d{3}\s?\d{3}\s*$", ErrorMessage = "The verification code must contain exactly 6 digits.")]
    public string? Code { get; init; }

    /// <summary>通行密钥断言的 JSON 形式（先经 <c>me/reauthentication/passkey-options</c> 取选项）。</summary>
    public JsonElement? Passkey { get; init; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var presented = (string.IsNullOrWhiteSpace(Code) ? 0 : 1) + (Passkey is { ValueKind: JsonValueKind.Object } ? 1 : 0);
        if (presented == 0)
            yield return new ValidationResult("Enter the verification code or a recovery code.", [nameof(Code)]);
        else if (presented > 1)
            yield return new ValidationResult("Enter only one kind of code.", [nameof(Code)]);
    }
}
