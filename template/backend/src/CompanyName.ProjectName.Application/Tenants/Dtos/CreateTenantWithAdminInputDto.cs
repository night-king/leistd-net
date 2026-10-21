using System.ComponentModel.DataAnnotations;
using CompanyName.ProjectName.Application.Tenants.Provider;
using CompanyName.ProjectName.Domain.Users.Policies;
using Leistd.MultiTenancy.Management.Dtos;

namespace CompanyName.ProjectName.Application.Tenants.Dtos;

/// <summary>创建租户入参：在组件的创建入参之上带租户管理员的初始凭据，开通时在新租户里建好管理员。</summary>
/// <remarks>
/// 租户管理的端点与编排由多租户组件提供（<c>MapTenantManagement&lt;CreateTenantWithAdminInputDto&gt;</c>），
/// 这两个字段是本项目的开通需要，由 <see cref="TenantSeeder"/> 从开通上下文里取回。
/// </remarks>
public record CreateTenantWithAdminInputDto : CreateTenantInputDto
{
    /// <summary>租户管理员邮箱。</summary>
    [Display(Name = "Admin email")]
    [Required(ErrorMessage = "{0} is required.")]
    [EmailAddress(ErrorMessage = "{0} has an invalid format.")]
    [MaxLength(256, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string AdminEmail { get; init; } = null!;

    /// <summary>租户管理员初始密码（仅存哈希）。</summary>
    [Display(Name = "Admin password")]
    [Required(ErrorMessage = "{0} is required.")]
    // 仅快速反馈；权威在服务端 PasswordPolicy
    [MinLength(PasswordPolicy.MinimumLength, ErrorMessage = "{0} must be at least {1} characters.")]
    [MaxLength(PasswordPolicy.MaximumLength, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string AdminPassword { get; init; } = null!;

    /// <summary>不输出初始密码与连接串：记录类型默认打印全部属性，随手写进日志就是泄露。</summary>
    public override string ToString() =>
        $"{nameof(CreateTenantWithAdminInputDto)} {{ Name = {Name}, DisplayName = {DisplayName}, AdminEmail = {AdminEmail} }}";
}
