#if (OpenIddictServer)
using CompanyName.ProjectName.Api.Auth.Sessions;

namespace CompanyName.ProjectName.Api.Options;

/// <summary>授权服务器的证书、签发方和令牌寿命，配置节 OAuth。</summary>
internal sealed class OAuthServerOptions
{
    public const string SectionName = "OAuth";

    /// <summary>使用本机开发证书，默认关闭；多副本部署使用共享文件证书。</summary>
    public bool UseDevelopmentCertificates { get; set; }

    /// <summary>签名证书；新旧证书可重叠登记，选用顺序由 OpenIddict 决定。</summary>
    public OAuthCertificate[] SigningCertificates { get; set; } = [];

    /// <summary>加密证书；旧证书保留到其授权码与刷新令牌全部过期。</summary>
    public OAuthCertificate[] EncryptionCertificates { get; set; } = [];

    /// <summary>访问令牌寿命，默认10分钟；整秒且长于 <see cref="AccessTokenRenewal.Lead"/>。</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>关闭 HTTPS 要求，仅供本机或测试调试。</summary>
    public bool DisableHttpsRequirement { get; set; }

    /// <summary>外部可访问的签发方地址；为空时使用请求 host，跨服务验证必须显式配置。</summary>
    public string? Issuer { get; set; }
}
#endif
