namespace CompanyName.ProjectName.Domain.Users.Constants;

public static class AdminConstant
{
    public const string RoleName = "Admin";

    /// <summary>租户初始化时创建的租户管理员用户名。</summary>
    /// <remarks>
    /// 播种方（<c>TenantSeeder</c>）与按约定查找该用户的一方（模拟登录）必须用同一个值：
    /// 各写一份字面量时，改了一处的表现是"模拟登录说找不到管理员"，而租户里明明有 admin。
    /// </remarks>
    public const string TenantAdminUsername = "admin";
}
