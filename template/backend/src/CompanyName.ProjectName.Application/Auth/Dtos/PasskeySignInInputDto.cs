using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>免口令登录：取选项时拿到的仪式凭据与浏览器交回的断言。</summary>
public sealed record PasskeySignInInputDto
{
    /// <summary>取选项时返回的仪式凭据。</summary>
    [Display(Name = "Passkey ceremony")]
    [Required(ErrorMessage = "{0} is required.")]
    [StringLength(128, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string Ceremony { get; init; } = null!;

    /// <summary><c>navigator.credentials.get()</c> 返回的凭据的 JSON 形式。</summary>
    [Required(ErrorMessage = "{0} is required.")]
    public JsonElement? Credential { get; init; }
}
