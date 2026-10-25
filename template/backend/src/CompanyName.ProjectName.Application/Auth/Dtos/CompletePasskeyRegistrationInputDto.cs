using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>完成登记：浏览器创建的凭据与本人起的名称。</summary>
public sealed record CompletePasskeyRegistrationInputDto
{
    /// <summary><c>navigator.credentials.create()</c> 返回的凭据的 JSON 形式（<c>PublicKeyCredential.toJSON()</c>）。</summary>
    [Required(ErrorMessage = "{0} is required.")]
    public JsonElement? Credential { get; init; }

    /// <summary>名称，便于在列表里区分；前端默认取设备与浏览器。</summary>
    [Display(Name = "Passkey name")]
    [Required(ErrorMessage = "{0} is required.")]
    [StringLength(128, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string Name { get; init; } = null!;
}
