using System.ComponentModel;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.ServiceClient.Options;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Leistd.MultiTenancy.ServiceClient.Stores;

// 远端库目录的短时缓存：常驻服务的逐库作业按秒轮询，不能每轮都回源控制面。
// 写法同 RemoteConnectionStringResolver：只用进程内一级缓存，同键并发回源合并，失败不缓存，过期即回源、不返回陈旧清单；
// 取消沿用 HybridCache：全部等待者都取消时回源才取消。全集与活跃清单分键，FailedTenants 随同一次回源的结果整体缓存。
// 远端存储在工厂里自开作用域取：工厂为所有等待者服务；按存储接口取再转目录，按目录接口取会拿到本类自己。
// 连接名来自 [ConnectionStringName]，不合法是编程错误，Normalize 直接抛 ArgumentException。
internal sealed class CachedTenantDatabaseDirectory(
    HybridCache cache,
    IServiceScopeFactory scopeFactory,
    IOptions<RemoteTenantConnectionClientOptions> options) : ITenantDatabaseDirectory
{
    public async Task<TenantDatabaseListResult> GetDatabasesAsync(
        string name,
        bool activeOnly,
        CancellationToken cancellationToken = default)
    {
        var normalized = TenantConnectionNames.Normalize(name);
        var lifetime = options.Value.DatabaseDirectoryCacheLifetime;

        var entry = await cache.GetOrCreateAsync(
            $"leistd:tenant-databases:{normalized}:{(activeOnly ? "active" : "all")}",
            (Directory: this, Name: normalized, ActiveOnly: activeOnly),
            static (state, ct) => state.Directory.ListRemoteAsync(state.Name, state.ActiveOnly, ct),
            new HybridCacheEntryOptions
            {
                Expiration = lifetime,
                LocalCacheExpiration = lifetime,
                Flags = HybridCacheEntryFlags.DisableDistributedCache
            },
            cancellationToken: cancellationToken);
        return entry.Result;
    }

    private async ValueTask<CachedListing> ListRemoteAsync(string name, bool activeOnly, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var directory = (ITenantDatabaseDirectory)scope.ServiceProvider.GetRequiredService<ITenantConnectionConfigurationStore>();
        return new CachedListing(await directory.GetDatabasesAsync(name, activeOnly, cancellationToken));
    }

    // HybridCache 只对标记不可变的类型承诺一级缓存复用实例；未标记时按文档可能每次命中都反序列化，
    // 且序列化后超过 MaximumPayloadBytes 的清单不进缓存。当前版本关闭二级缓存时实测也复用，但不依赖这一实现细节
    [ImmutableObject(true)]
    internal sealed record CachedListing(TenantDatabaseListResult Result);
}
