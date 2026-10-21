using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace CompanyName.ProjectName.Infrastructure;

/// <summary>提供本服务的 Data Protection 密钥环注册。</summary>
public static class DataProtectionExtensions
{
    /// <summary>注册持久化的 Data Protection 密钥环：配置了 Redis 时存 Redis，否则存文件系统。</summary>
    /// <remarks>
    /// <para>除认证 Cookie 与防伪令牌外，启用本地身份时还有三类落库数据用这套密钥环加密：
    /// 控制库里租户独立库的连接串、设置表里的机密设置（如发信口令）、用户的两步验证密钥。
    /// 读写同一个库的进程（API、DbMigrator）必须共享同一密钥环与应用名，否则一方写入的
    /// 密文另一方解不开：租户连接串解不开时该租户的请求与迁移都会被拒绝。</para>
    /// <para>显式指定存储位置后，框架不再自动对密钥做静态加密；存储位置本身须只允许本服务访问。
    /// 需要静态加密时，在这里按官方的 <c>ProtectKeysWith*</c>（如 <c>ProtectKeysWithCertificate</c>）追加。</para>
    /// <para>密钥丢失等于这些数据丢失：开发环境以外必须把密钥持久化到共享且有备份的位置——
    /// 配置 Redis，或用 <c>DataProtection:KeysPath</c> 把各进程指向同一个持久目录；两者都没配时启动失败。
    /// <c>KeysPath</c> 只解决密钥共享：多副本部署仍须配置 Redis，分布式缓存与锁依赖它。
    /// 只有开发环境回落到内容根下的本地目录：容器里的这个目录随容器重建而消失，多副本之间也互不共享。</para>
    /// </remarks>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置</param>
    /// <param name="environment">宿主环境：开发环境才允许回落到内容根下的本地密钥目录</param>
    public static IServiceCollection AddMyProjectDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var dataProtection = services.AddDataProtection().SetApplicationName("MyProject");

        // 连接串使用 StackExchange.Redis 原生格式（host:port,password=...,ssl=true）
        var redisConnStr = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrEmpty(redisConnStr))
        {
            dataProtection.PersistKeysToStackExchangeRedis(
                ConnectionMultiplexer.Connect(redisConnStr),
                "DataProtection-Keys");
        }
        else
        {
            var configuredPath = configuration["DataProtection:KeysPath"];
            if (string.IsNullOrWhiteSpace(configuredPath) && !environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "Data Protection keys must be persisted to a shared, backed-up location outside Development: " +
                    "set ConnectionStrings:Redis, or DataProtection:KeysPath to a persistent directory shared by the API " +
                    "and DbMigrator. Losing the keys makes sign-in cookies, tenant connection strings, secret settings " +
                    "and two-factor keys unreadable.");
            }

            var keysPath = string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine(environment.ContentRootPath, "DataProtection-Keys")
                : configuredPath;
            Directory.CreateDirectory(keysPath);
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }

        return services;
    }
}
