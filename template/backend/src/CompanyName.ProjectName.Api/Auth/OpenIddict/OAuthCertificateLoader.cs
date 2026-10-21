using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CompanyName.ProjectName.Api.Options;

namespace CompanyName.ProjectName.Api.Auth.OpenIddict;

/// <summary>
/// 在组合期逐张加载令牌证书：任何一张文件损坏、口令错误、没有 RSA 私钥或重复，都以带下标的键名报错
/// （集合与路径的结构由 <see cref="OAuthServerOptionsValidator"/> 先行校验）。
/// </summary>
/// <remarks>
/// 证书集合用于重叠轮换（新旧同时登记），一张坏了不能静默跳过——跳过签名证书会让 JWKS 少一个 kid，
/// 跳过加密证书会让已签发的授权码与刷新令牌无法解密，两者都要等到请求失败才暴露。
/// </remarks>
internal static class OAuthCertificateLoader
{
    public static X509Certificate2[] Load(IReadOnlyList<OAuthCertificate> certificates, string section)
    {
        var loaded = new List<X509Certificate2>(certificates.Count);
        try
        {
            for (var index = 0; index < certificates.Count; index++)
            {
                var key = $"{section}:{index}:Path";
                X509Certificate2 certificate;
                try
                {
                    certificate = X509CertificateLoader.LoadPkcs12FromFile(certificates[index].Path, certificates[index].Password);
                }
                catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
                {
                    // 只给异常类型：消息可能含路径以外的细节，口令绝不进诊断
                    throw new InvalidOperationException(
                        $"{key} could not be loaded as a PKCS#12 certificate ({exception.GetType().Name}); check the file and its password.");
                }
                loaded.Add(certificate);
                using var rsa = certificate.HasPrivateKey ? certificate.GetRSAPrivateKey() : null;
                if (rsa is null)
                    throw new InvalidOperationException($"{key} must contain an RSA private key.");
            }
            if (loaded.Select(certificate => certificate.Thumbprint).Distinct(StringComparer.OrdinalIgnoreCase).Count() != loaded.Count)
                throw new InvalidOperationException($"{section} contains the same certificate more than once.");
            return [.. loaded];
        }
        catch
        {
            // 启动失败时释放已加载的证书（私钥句柄）
            foreach (var certificate in loaded) certificate.Dispose();
            throw;
        }
    }
}
