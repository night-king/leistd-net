using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>重命名通行密钥。</summary>
public sealed record RenamePasskeyInputDto
{
    /// <summary>新名称。</summary>
    [Display(Name = "Name")]
    [Required(ErrorMessage = "{0} is required.")]
    [StringLength(128, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string Name { get; init; } = null!;
}
