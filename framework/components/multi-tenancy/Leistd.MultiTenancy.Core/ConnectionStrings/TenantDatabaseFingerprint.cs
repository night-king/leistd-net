using System.Security.Cryptography;
using System.Text;

namespace Leistd.MultiTenancy.ConnectionStrings;

/// <summary>连接配置的指纹：判定"两处用的是不是同一份连接配置"。</summary>
/// <remarks>
/// <para>宿主库、迁移目标与运行时库目录共用此算法，指纹才可比；指纹不可逆推连接串，可出现在日志与响应里。</para>
/// <para>判的是连接配置相同，不是物理库相同：同一个库用不同凭据、不同键值对顺序或等价主机名写法，
/// 会得到不同指纹并被当成两个库，因此一个库应只登记一份连接配置。</para>
/// </remarks>
public static class TenantDatabaseFingerprint
{
    /// <summary>算连接串的 SHA-256 指纹（十六进制）。</summary>
    public static string Of(string connectionString)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connectionString)));
}
