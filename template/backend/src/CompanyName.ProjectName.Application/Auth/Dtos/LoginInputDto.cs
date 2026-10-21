using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>登录请求 DTO。</summary>
public record LoginInputDto
{
    /// <summary>用户名或邮箱。</summary>
    [Display(Name = "Username or email")]
    [Required(ErrorMessage = "{0} is required.")]
    [StringLength(256, MinimumLength = 3, ErrorMessage = "{0} must be between {2} and {1} characters.")]
    public string UsernameOrEmail { get; init; } = null!;

    /// <summary>密码。</summary>
    [Display(Name = "Password")]
    [Required(ErrorMessage = "{0} is required.")]
    [StringLength(100, MinimumLength = 6, ErrorMessage = "{0} must be between {2} and {1} characters.")]
    public string Password { get; init; } = null!;
}
