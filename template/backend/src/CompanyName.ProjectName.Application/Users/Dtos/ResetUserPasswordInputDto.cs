#if (LocalIdentity)
using System.ComponentModel.DataAnnotations;
using CompanyName.ProjectName.Domain.Users.Policies;

namespace CompanyName.ProjectName.Application.Users.Dtos;

/// <summary>重置用户密码：<see cref="Password"/> 是给对方设的新口令；管理员的会话不够新时另带自己的当前口令。</summary>
public record ResetUserPasswordInputDto
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
}
#endif
