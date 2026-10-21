namespace CompanyName.ProjectName.Infrastructure.Persistence.EntityConfigurations;

/// <summary>User 表的数据库检查约束名。</summary>
/// <remarks>
/// 提成常量：约束名出现在实体配置、迁移与测试三处，字面量拼错的表现是"约束看起来在、其实没建"。
/// </remarks>
internal static class UserCheckConstraints
{
    /// <summary><c>IsSuperAdmin</c> 只能出现在宿主行（<c>TenantId IS NULL</c>）</summary>
    internal const string SuperAdminIsHostOnly = "CK_User_SuperAdminIsHostOnly";
}
