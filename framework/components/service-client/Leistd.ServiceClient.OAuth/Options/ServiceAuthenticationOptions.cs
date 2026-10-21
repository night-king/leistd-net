namespace Leistd.ServiceClient.OAuth.Options;

/// <summary>工作负载的全局 OpenID Connect 客户端身份。</summary>
public sealed class ServiceAuthenticationOptions
{
    /// <summary>默认配置节。</summary>
    public const string SectionName = "Leistd:ServiceAuth";
    /// <summary>发现文档的签发者地址。</summary>
    public string Authority { get; set; } = string.Empty;
    /// <summary>与来源 API 受众一致的客户端标识。</summary>
    public string ClientId { get; set; } = string.Empty;
    /// <summary>来自密钥管理的客户端密钥。</summary>
    public string ClientSecret { get; set; } = string.Empty;
}
