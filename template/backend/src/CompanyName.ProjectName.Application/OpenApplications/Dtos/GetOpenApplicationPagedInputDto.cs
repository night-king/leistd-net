#if (LocalIdentity)
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Leistd.Data.Paging;

namespace CompanyName.ProjectName.Application.OpenApplications.Dtos;

/// <summary>获取开放应用分页列表输入 DTO。</summary>
public record GetOpenApplicationPagedInputDto : PageRequest
{
    [AllowNull]
    public override string Sorting
    {
        get => string.IsNullOrWhiteSpace(field) ? $"{nameof(OpenApplicationQueryItem.CreationTime)} desc" : field;
        init;
    }

    /// <summary>搜索关键字。</summary>
    [Display(Name = "Search keyword")]
    [MaxLength(256, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? Keyword { get; init; }

    /// <summary>应用类型。</summary>
    public string? ApplicationType { get; init; }

    /// <summary>客户端类型。</summary>
    public string? ClientType { get; init; }
}
#endif
