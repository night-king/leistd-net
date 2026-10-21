using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Leistd.MultiTenancy.EntityFrameworkCore.ConnectionStrings;

// 连接串写入时加密、读取时解密；密钥环沿用宿主的 Data Protection 配置，本组件不管理密钥。
// purpose 固定：改动会让所有已存密文不可解密。
internal sealed class TenantConnectionStringProtector(IDataProtectionProvider provider)
{
    public const string Purpose = "Leistd.MultiTenancy.TenantConnectionString.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string connectionString) => _protector.Protect(connectionString);

    public string Unprotect(Guid tenantId, string name, string protectedConnectionString)
    {
        try
        {
            return _protector.Unprotect(protectedConnectionString);
        }
        catch (CryptographicException exception)
        {
            // 密钥丢失、密钥环未共享或密文被篡改；拒绝而不是回退到宿主自己的库。消息只带租户与连接名
            throw new InvalidOperationException(
                $"The '{name}' connection string of tenant '{tenantId}' could not be decrypted with the current " +
                "Data Protection key ring. Check that this process shares the key ring and application name " +
                "of the process that wrote it.",
                exception);
        }
    }
}
