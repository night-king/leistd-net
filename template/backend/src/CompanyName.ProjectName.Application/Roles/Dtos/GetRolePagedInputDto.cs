using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Leistd.Data.Paging;
using CompanyName.ProjectName.Domain.Users.Entities;

namespace CompanyName.ProjectName.Application.Roles.Dtos;

/// <summary>获取角色分页列表输入 DTO。</summary>
public record GetRolePagedInputDto : PageRequest
{
    [AllowNull]
    public override string Sorting
    {
        get => string.IsNullOrWhiteSpace(field) ? $"{nameof(Role.Sort)} asc" : field;
        init;
    }

    /// <summary>搜索关键字（名称、显示名称）。</summary>
    [Display(Name = "Search keyword")]
    [MaxLength(256, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? Keyword { get; init; }
}
