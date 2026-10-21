#if (LocalIdentity)
using System.Data.Common;
using Leistd.ExceptionHandling;
using Leistd.MultiTenancy.Errors;
using Leistd.MultiTenancy.Management.Provisioning;

namespace CompanyName.ProjectName.Infrastructure.TenantConnections.Provider;

/// <summary>按 PostgreSQL 的 SQLSTATE 把开通失败翻译成调用方能照着改的 400。</summary>
/// <remarks>
/// <para>错误码表是各数据库自己的方言，框架只定义错误码与译文，映射由本项目给出——本项目已经选定 PostgreSQL。
/// 换数据库时改这一个类。</para>
/// <para><b>只翻译确定由调用方输入引起的四种</b>：连不上、库不存在、没迁移、凭据不对。其余（库重启、连接数耗尽、
/// 序列化失败……）返回 <see langword="null"/>，让原异常进统一的 5xx——那些不是调用方改连接串能解决的，
/// 报成 400 会让客户端既不重试也不告警。</para>
/// <para>消息不回显连接串、主机与端口：错误会同时进响应体、前端提示与服务端日志。</para>
/// </remarks>
internal sealed class PostgresTenantDatabaseErrorDescriber : ITenantDatabaseErrorDescriber
{
    /// <inheritdoc />
    public BusinessException? Describe(Exception error)
    {
        // 逐层往内找 DbException：驱动异常常被 EF 或工作单元包上几层
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is not DbException database)
            {
                continue;
            }

            // 连接阶段失败（主机不可达、端口拒绝）不是服务端返回的错误，没有 SQLSTATE
            if (string.IsNullOrEmpty(database.SqlState))
            {
                return Describe(
                    "The tenant's dedicated database is unreachable. Check that the host and port are reachable and the database server is running.",
                    MultiTenancyErrorCodes.DedicatedDatabaseUnreachable);
            }

            return database.SqlState switch
            {
                // invalid_catalog_name：连上了服务器，但那个库不存在
                "3D000" => Describe(
                    "The database named in the connection string does not exist. Create and migrate it first, then create the tenant.",
                    MultiTenancyErrorCodes.DedicatedDatabaseMissing),

                // undefined_table：库在，但没迁移过
                "42P01" => Describe(
                    "The database exists but has not been migrated; its tables are missing. Migrate it first.",
                    MultiTenancyErrorCodes.DedicatedDatabaseNotMigrated),

                // invalid_authorization_specification / invalid_password
                "28000" or "28P01" => Describe(
                    "The database rejected the credentials in the connection string.",
                    MultiTenancyErrorCodes.DedicatedDatabaseRejected),

                // 其余 SQLSTATE 不是调用方能改的，交给统一的 5xx
                _ => null
            };
        }

        return null;
    }

    private static BusinessException Describe(string message, string code)
        => new BusinessException(code, message);
}
#endif
