namespace Leistd.MultiTenancy.ConnectionStrings;

/// <summary>远端解析结果的路由缓存（默认配置节 <c>Leistd:MultiTenancy:Routing</c>）。</summary>
/// <remarks>
/// <para><see cref="CacheLifetime"/> 默认 <see cref="DefaultCacheLifetime"/>，可在配置中覆盖。改租户数据落点的流程是：
/// 停用租户 → 等待排空 → 迁移数据 → 改路由 → 重新启用，排空要等 <c>max(Access Token 有效期, 本 TTL)</c>。</para>
/// <para>与 Access Token 有效期彼此独立：令牌决定已停用租户还能被访问多久，TTL 决定已改的路由还会被沿用多久。</para>
/// </remarks>
public sealed class TenantRouteCacheOptions
{
    /// <summary>默认配置节路径 <c>Leistd:MultiTenancy:Routing</c>。</summary>
    public const string SectionName = "Leistd:MultiTenancy:Routing";

    /// <summary>默认路由缓存生存期：10 分钟。</summary>
    public static readonly TimeSpan DefaultCacheLifetime = TimeSpan.FromMinutes(10);

    /// <summary>TTL 上限：更长的 TTL 会把切换流程的排空等待拖到不可操作。</summary>
    public static readonly TimeSpan MaximumCacheLifetime = TimeSpan.FromHours(1);

    /// <summary>路由缓存生存期，须大于 0 且不超过 <see cref="MaximumCacheLifetime"/>。</summary>
    public TimeSpan CacheLifetime { get; set; } = DefaultCacheLifetime;
}
