using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.ServiceClient.Options;

namespace Leistd.MultiTenancy.ServiceClient.Options;

/// <summary>远端租户连接存储的客户端配置，配置节 <c>Leistd:ServiceClients:{服务名}</c>。</summary>
public sealed class RemoteTenantConnectionClientOptions : ServiceClientOptions
{
    /// <summary>默认路由前缀，与组件文档里映射 <c>MapTenantConnections</c> 的示例一致。</summary>
    public const string DefaultRoutePrefix = "/api/v1/tenant-connections";

    /// <summary>默认库目录缓存期限：30 秒。</summary>
    public static readonly TimeSpan DefaultDatabaseDirectoryCacheLifetime = TimeSpan.FromSeconds(30);

    /// <summary>控制面映射 <c>MapTenantConnections</c> 时用的路由前缀；与控制面不一致时首次解析即失败，不会静默连到别的库。</summary>
    public string RoutePrefix { get; set; } = DefaultRoutePrefix;

    /// <summary>远端库目录（<see cref="ITenantDatabaseDirectory"/>）的进程内缓存期限，须大于 0 且不超过
    /// <see cref="TenantRouteCacheOptions.MaximumCacheLifetime"/>。</summary>
    /// <remarks>
    /// 新增、停用或改路由的库最多晚一个期限出现在逐库作业的清单里；进库用的连接另由路由缓存
    /// （<see cref="TenantRouteCacheOptions.CacheLifetime"/>）决定，不随本期限变短。
    /// </remarks>
    public TimeSpan DatabaseDirectoryCacheLifetime { get; set; } = DefaultDatabaseDirectoryCacheLifetime;
}
