namespace Leistd.MultiTenancy.Context;

/// <summary>租户在日志上下文中使用的键名常量。</summary>
public static class TenantLogKeys
{
    /// <summary>日志上下文中的当前租户键名；值为租户 Id，宿主为 <see langword="null"/>。</summary>
    /// <remarks>
    /// 由 <see cref="ICurrentTenant.Change"/> 写入：凡切换租户的入口（HTTP 解析、Hub 与后台任务的环境上下文、
    /// 逐租户作业、令牌端点）都在同一个键下，日志按此键即可按租户过滤。
    /// </remarks>
    public const string TenantId = "leistd.tenantId";
}
