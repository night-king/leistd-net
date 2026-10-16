#if (LocalIdentity)
using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>完成外部账号绑定：会话不够新时带上本人的当前口令。</summary>
public record CompleteExternalLinkInputDto
{
    /// <summary>本人的当前口令；会话刚认证过时不用带。</summary>
    [Display(Name = "Password")]
    [StringLength(256, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? Password { get; init; }
}
#endif
