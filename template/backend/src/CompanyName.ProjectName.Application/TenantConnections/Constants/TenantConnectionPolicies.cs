#if (OpenIddictServer)
namespace CompanyName.ProjectName.Application.TenantConnections.Constants;

/// <summary>租户连接端点的授权策略名。</summary>
/// <remarks>
/// 与上面的 scope 成对：scope 是令牌里的凭据，策略名是端点上引用它的键。
/// 提成常量而不是各处写字面量——这两个名字出现在策略注册、端点映射与测试三处，
/// 拼错任何一处的表现是"端点永远 403"，而编译器不会提示。
/// </remarks>
public static class TenantConnectionPolicies
{
    /// <summary>读取单个租户的运行时路由（资源服务回源用）</summary>
    public const string RuntimeRead = "TenantConnection.RuntimeRead";

    /// <summary>枚举迁移目标（DbMigrator 用）</summary>
    public const string MigrationRead = "TenantConnection.MigrationRead";
}
#endif
