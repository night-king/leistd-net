using Leistd.MultiTenancy.Context;

namespace Leistd.MultiTenancy.Extensions;

/// <summary>把外部标识限定到当前租户。</summary>
public static class CurrentTenantKeyExtensions
{
    /// <summary>宿主视角的标识前缀。</summary>
    public const string HostScope = "host";

    /// <summary>在标识前拼上当前租户段，使同一个逻辑名在不同租户下互不可见。</summary>
    /// <remarks>
    /// 用于需按租户隔离的缓存、锁和幂等键；不替代数据库查询过滤器。
    /// 租户注册表、连接配置等跨上下文共享的数据不应添加当前租户段。
    /// </remarks>
    /// <example>
    /// <code>
    /// // 产出 "{租户 Id:N}:catalog:category:{id:N}"，交给缓存、分布式锁或幂等存储做键
    /// var key = currentTenant.ScopeKey($"catalog:category:{id:N}");
    /// </code>
    /// </example>
    /// <param name="currentTenant">当前租户上下文。</param>
    /// <param name="key">业务标识，不含租户段。</param>
    /// <returns>宿主视角为 <c>host:{key}</c>，租户视角为 <c>{租户 Id:N}:{key}</c>。</returns>
    public static string ScopeKey(this ICurrentTenant currentTenant, string key)
    {
        ArgumentNullException.ThrowIfNull(currentTenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // 租户 Id 的 N 格式是 32 位十六进制，与 host 不会撞。
        return currentTenant.Id is { } tenantId
            ? $"{tenantId:N}:{key}"
            : $"{HostScope}:{key}";
    }
}
