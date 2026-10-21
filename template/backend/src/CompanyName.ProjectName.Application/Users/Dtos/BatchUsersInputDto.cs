using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Users.Dtos;

/// <summary>用户批量操作输入 DTO。</summary>
public record BatchUsersInputDto
{
    /// <summary>一次批量操作最多处理的用户数。</summary>
    public const int MaxCount = 100;

    /// <summary>目标用户 Id；重复的 Id 只处理一次。</summary>
    [Display(Name = "Users")]
    [Required(ErrorMessage = "{0} is required.")]
    [MinLength(1, ErrorMessage = "{0} is required.")]
    [MaxLength(MaxCount, ErrorMessage = "{0} cannot contain more than {1} items.")]
    public List<Guid> Ids { get; init; } = null!;
}
