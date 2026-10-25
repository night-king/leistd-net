using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CompanyName.ProjectName.Application.Auth.Passkeys;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>登录第二步：恰好提交一种方式的凭证（通行密钥断言、身份验证器验证码、邮箱验证码或恢复码）。</summary>
/// <remarks>多给一种是校验错误：按优先级挑一个会悄悄用掉本人没打算用的恢复码，也让判定依赖字段顺序。</remarks>
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

#if (Email)
    /// <summary>发到邮箱的 6 位验证码（先经 <c>two-factor/email-code</c> 发码）。</summary>
    [Display(Name = "Email verification code")]
    [RegularExpression(@"^\s*\d{6}\s*$", ErrorMessage = "The email verification code must contain exactly 6 digits.")]
    public string? EmailCode { get; init; }

#endif
    /// <summary><c>navigator.credentials.get()</c> 返回的通行密钥断言的 JSON 形式（先经 <c>two-factor/passkey-options</c> 取选项）。</summary>
    public JsonElement? Passkey { get; init; }

    /// <summary>恢复码（手机不在身边时用）。</summary>
    [Display(Name = "Recovery code")]
    [StringLength(64, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? RecoveryCode { get; init; }

    /// <inheritdoc />
    /// <remarks>"恰好给一种"没有对应的内置特性；文案与特性文案一样是本地化键。</remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var presented = new[]
        {
            Code,
#if (Email)
            EmailCode,
#endif
            RecoveryCode
        }.Count(value => !string.IsNullOrWhiteSpace(value)) + (PasskeyCredentialJson.IsPresented(Passkey) ? 1 : 0);
        if (presented == 0)
            yield return new ValidationResult("Enter the verification code or a recovery code.", [nameof(Code)]);
        else if (presented > 1)
            yield return new ValidationResult("Enter only one kind of code.", [nameof(Code)]);

        foreach (var error in PasskeyCredentialJson.ValidateShape(Passkey, nameof(Passkey)))
            yield return error;
    }
}
