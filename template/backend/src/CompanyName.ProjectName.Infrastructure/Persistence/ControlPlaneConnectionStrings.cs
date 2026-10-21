#if (LocalIdentity)
using Leistd.Data.Connections;
using Microsoft.Extensions.Configuration;

namespace CompanyName.ProjectName.Infrastructure.Persistence;

/// <summary>控制面连接串的回落链：<c>IdentityControl</c> → <c>Default</c>。</summary>
/// <remarks>
/// 运行时、DbMigrator 与设计时工厂共用此回落链，保证迁移目标与运行时控制面一致。
/// 空白值按未配置处理；两级均未配置时返回 <see langword="null"/>，由宿主启动校验
/// 报告缺失的配置键。
/// </remarks>
public static class ControlPlaneConnectionStrings
{
    /// <summary>按回落链取控制面连接串；两级都未配置时返回 <see langword="null"/></summary>
    /// <param name="configuration">配置源</param>
    public static string? GetControlPlaneConnectionString(this IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return Configured(configuration.GetConnectionString("IdentityControl"))
            ?? Configured(configuration.GetConnectionString(ConnectionStringNames.Default));
    }

    private static string? Configured(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
#endif
