#if (LocalIdentity)
using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Users.Dtos;

/// <summary>重置用户两步验证：管理员的会话不够新时，带上管理员自己的当前口令。</summary>
public record ResetUserTwoFactorInputDto
{
    /// <summary>管理员本人的当前口令；会话刚认证过时不用带。</summary>
    [Display(Name = "Password")]
    [StringLength(256, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? Password { get; init; }
}
#endif
