using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>登录第二步：取用通行密钥验证的断言选项。</summary>
public sealed record TwoFactorPasskeyOptionsInputDto
{
    /// <summary>第一步返回的凭据。</summary>
    [Display(Name = "Two-factor token")]
    [Required(ErrorMessage = "{0} is required.")]
    [StringLength(128, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string Token { get; init; } = null!;
}
