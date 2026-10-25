using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CompanyName.ProjectName.Application.Auth.Policies;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>开始设置两步验证：首次启用，或已启用时换绑身份验证器。</summary>
/// <remarks>
/// <para>会话刚认证过（见 <c>IReauthenticationGuard.EnsureRecentAsync</c>）时不用带凭证；
/// 否则服务端以 <c>Auth:ReauthenticationRequired</c> 要求确认身份再来。</para>
/// <para>换绑还要再出示一次现有的第二因素：现有验证器上的验证码同时算作确认身份，只校验一次，不必再带口令；
/// 验证器丢了时用一个恢复码（当场用掉），恢复码不算确认身份，会话不够新时仍要口令。</para>
/// </remarks>
public sealed record BeginTwoFactorSetupInputDto : IValidatableObject
{
    /// <summary>当前口令；会话不够新时才需要。</summary>
    [Display(Name = "Password")]
    [StringLength(256, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? Password { get; init; }

    /// <summary>换绑时：现有身份验证器上的 6 位验证码，同时确认身份。</summary>
    [Display(Name = "Verification code")]
    [RegularExpression(@"^\s*\d{3}\s?\d{3}\s*$", ErrorMessage = "The verification code must contain exactly 6 digits.")]
    public string? Code { get; init; }

    /// <summary>换绑时：验证器丢了，用一个恢复码代替验证码。</summary>
    [Display(Name = "Recovery code")]
    [StringLength(64, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? RecoveryCode { get; init; }

    /// <summary>通行密钥断言的 JSON 形式（先经 <c>me/reauthentication/passkey-options</c> 取选项），与其他凭证三选一；会话刚认证过时不用带。</summary>
    public JsonElement? Passkey { get; init; }

    /// <inheritdoc />
    /// <remarks>验证码本身就是确认身份的凭证，与口令一次只出示一种。</remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        ReauthenticationProof.ValidateOneAtATime(Password, Code, Passkey, nameof(Code));
}
