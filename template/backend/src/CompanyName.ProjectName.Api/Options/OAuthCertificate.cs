#if (OpenIddictServer)
namespace CompanyName.ProjectName.Api.Options;

/// <summary>PKCS#12 文件证书；口令由部署注入。</summary>
internal sealed class OAuthCertificate
{
    public string Path { get; set; } = string.Empty;
    public string? Password { get; set; }
}
#endif
