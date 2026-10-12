#if (LocalIdentity)
using System.ComponentModel.DataAnnotations;
using CompanyName.ProjectName.Domain.Users.Policies;

namespace CompanyName.ProjectName.Application.Users.Dtos;

/// <summary>重置用户密码输入 DTO。</summary>
public record ResetUserPasswordInputDto
{
    [Display(Name = "Password")]
    [Required(ErrorMessage = "{0} is required.")]
    // 仅快速反馈；权威在服务端 PasswordPolicy
    [StringLength(PasswordPolicy.MaximumLength, MinimumLength = PasswordPolicy.MinimumLength,
        ErrorMessage = "{0} must be between {2} and {1} characters.")]
    public string Password { get; init; } = null!;
}
#endif
