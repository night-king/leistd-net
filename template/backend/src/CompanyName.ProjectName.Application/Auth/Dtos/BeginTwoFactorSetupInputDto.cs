using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>开始设置两步验证：首次启用，或已启用时换绑身份验证器。</summary>
/// <remarks>
/// <para>会话刚认证过（见 <c>IReauthenticationGuard.EnsureRecentlyAuthenticatedAsync</c>）时不用带口令；
/// 否则服务端以 <c>Auth:ReauthenticationRequired</c> 要求带上当前口令再来。</para>
/// <para>换绑还要再出示一次第二因素：验证码，或者验证器丢了时用一个恢复码（当场用掉）。</para>
/// </remarks>
public sealed record BeginTwoFactorSetupInputDto
{
    /// <summary>当前口令；会话不够新时才需要。</summary>
    [Display(Name = "Password")]
    [StringLength(256, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? Password { get; init; }

    /// <summary>换绑时：现有身份验证器上的 6 位验证码。</summary>
    [Display(Name = "Verification code")]
    [RegularExpression(@"^\s*\d{3}\s?\d{3}\s*$", ErrorMessage = "The verification code must contain exactly 6 digits.")]
    public string? Code { get; init; }

    /// <summary>换绑时：验证器丢了，用一个恢复码代替验证码。</summary>
    [Display(Name = "Recovery code")]
    [StringLength(64, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? RecoveryCode { get; init; }
}
