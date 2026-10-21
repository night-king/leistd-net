using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Leistd.Data.Paging;
using CompanyName.ProjectName.Domain.Users.Entities;

namespace CompanyName.ProjectName.Application.Users.Dtos;

/// <summary>获取用户分页列表输入 DTO。</summary>
public record GetUserPagedInputDto : PageRequest, IValidatableObject
{
    [AllowNull]
    public override string Sorting
    {
        get => string.IsNullOrWhiteSpace(field) ? $"{nameof(User.CreationTime)} desc" : field;
        init;
    }

    /// <summary>单个角色名的长度上限，与角色名的持久化约束一致。</summary>
    private const int RoleNameMaxLength = 64;

    /// <summary>搜索关键字（用户名、邮箱、显示名称）。</summary>
    [Display(Name = "Search keyword")]
    [MaxLength(256, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? Keyword { get; init; }

    /// <summary>是否启用。</summary>
    [Display(Name = "Active status")]
    public bool? IsActive { get; init; }

#if (LocalIdentity)
    /// <summary>邮箱是否已验证。</summary>
    [Display(Name = "Email verification status")]
    public bool? IsEmailVerified { get; init; }
#endif

    /// <summary>角色名称（多选，命中任一角色即匹配）。单项长度上限见 <see cref="RoleNameMaxLength"/>。</summary>
    [Display(Name = "Roles")]
    [MaxLength(20, ErrorMessage = "{0} cannot contain more than {1} items.")]
    public List<string>? Roles { get; init; }

    /// <inheritdoc />
    /// <remarks>逐项长度没有对应的内置特性；文案与特性文案一样是本地化键。</remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Roles?.Exists(role => role?.Trim().Length > RoleNameMaxLength) == true)
            yield return new ValidationResult("Each role name cannot exceed 64 characters.", [nameof(Roles)]);
    }
}
