using System.Text.Json;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>交给浏览器的 WebAuthn 选项（<c>PublicKeyCredentialCreationOptions</c> 或 <c>PublicKeyCredentialRequestOptions</c> 的 JSON 形式）。</summary>
public sealed record PasskeyOptionsOutputDto
{
    /// <summary>选项原文，按协议库的规则序列化，原样传给 <c>navigator.credentials</c>。</summary>
    public JsonElement Options { get; init; }

    /// <summary>免口令登录的仪式凭据，完成时原样交回；其他仪式为 null。</summary>
    public string? Ceremony { get; init; }
}
