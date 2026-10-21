#if (LocalIdentity)
using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.OpenApplications.Dtos;

/// <summary>创建开放应用输入 DTO。</summary>
public record CreateOpenApplicationInputDto : IValidatableObject
{
    /// <summary>Client ID</summary>
    [Display(Name = "Client ID")]
    [Required(ErrorMessage = "{0} is required.")]
    [MaxLength(256, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string ClientId { get; init; } = null!;

    /// <summary>显示名称。</summary>
    [Display(Name = "Display name")]
    [MaxLength(256, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? DisplayName { get; init; }

    /// <summary>应用类型。</summary>
    [Display(Name = "Application type")]
    [Required(ErrorMessage = "{0} is required.")]
    [RegularExpression("^(web|native|service)$", ErrorMessage = "{0} is not an allowed value.")]
    public string ApplicationType { get; init; } = null!;

    /// <summary>客户端类型。</summary>
    [Display(Name = "Client type")]
    [Required(ErrorMessage = "{0} is required.")]
    [RegularExpression("^(public|confidential)$", ErrorMessage = "{0} is not an allowed value.")]
    public string ClientType { get; init; } = null!;

    /// <summary>Redirect URIs</summary>
    public List<string> RedirectUris { get; init; } = [];

    /// <summary>Post Logout Redirect URIs</summary>
    public List<string> PostLogoutRedirectUris { get; init; } = [];

    /// <summary>授权能力。</summary>
    public List<string> Permissions { get; init; } = [];

    /// <summary>要求。</summary>
    public List<string> Requirements { get; init; } = [];

    /// <summary>
    /// 会话绑定：授权码与刷新令牌依赖签发时的 Identity 会话（见 <see cref="OpenApplicationSettings.SessionBound"/>）。
    /// 必须显式给出；浏览器 BFF 类客户端应为 <c>true</c>。
    /// </summary>
    [Display(Name = "Session bound")]
    [Required(ErrorMessage = "{0} is required.")]
    public bool? SessionBound { get; init; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        UpdateOpenApplicationInputDto.ValidateUris(RedirectUris, PostLogoutRedirectUris);
}
#endif
