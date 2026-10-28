using System.ComponentModel;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace Leistd.MultiTenancy.ConnectionStrings;

// 路由与物理库清单共用进程内的有界旧值；HybridCache 只负责单飞，不使用请求作用域保存状态。
internal sealed class TenantRouteCache(HybridCache cache, TimeProvider clock, ILogger logger)
{
    public async ValueTask<T> GetAsync<T>(string key, TimeSpan freshFor, TimeSpan maxStaleness,
        Func<CancellationToken, ValueTask<T>> fetch, Func<Exception, bool> unavailable, CancellationToken cancellationToken)
    {
        var entry = await ReadAsync<T>(key, cancellationToken);
        if (entry is not null && clock.GetUtcNow() < entry.RetryAt) return entry.Value;
        return await cache.GetOrCreateAsync(key + ":fetch", async ct =>
        {
            var current = await ReadAsync<T>(key, ct);
            if (current is not null && clock.GetUtcNow() < current.RetryAt) return current.Value;
            Entry<T> result;
            try
            {
                var value = await fetch(ct);
                var now = clock.GetUtcNow();
                result = new Entry<T>(value, now, now + freshFor, now + freshFor + maxStaleness);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (!unavailable(exception))
                {
                    await cache.RemoveAsync(key, ct);
                    throw;
                }
                var previous = await ReadAsync<T>(key, ct);
                if (previous is null || clock.GetUtcNow() >= previous.HardDeadline) throw;
                var retry = clock.GetUtcNow().AddSeconds(30);
                result = previous with { RetryAt = retry < previous.HardDeadline ? retry : previous.HardDeadline };
                logger.LogWarning("Control-plane dependency {Dependency} is unavailable; using state stale for {Staleness}.",
                    key, clock.GetUtcNow() - (previous.ConfirmedAt + freshFor));
            }
            var ttl = result.HardDeadline - clock.GetUtcNow();
            if (ttl > TimeSpan.Zero)
                await cache.SetAsync(key, result, new HybridCacheEntryOptions
                {
                    Flags = HybridCacheEntryFlags.DisableDistributedCache,
                    Expiration = ttl, LocalCacheExpiration = ttl
                }, cancellationToken: ct);
            return result.Value;
        }, new HybridCacheEntryOptions
        {
            Flags = HybridCacheEntryFlags.DisableDistributedCache | HybridCacheEntryFlags.DisableLocalCacheWrite
        }, cancellationToken: cancellationToken);
    }

    private ValueTask<Entry<T>?> ReadAsync<T>(string key, CancellationToken ct) =>
        cache.GetOrCreateAsync<Entry<T>?>(key, static _ => ValueTask.FromResult<Entry<T>?>(null), new HybridCacheEntryOptions
        {
            Flags = HybridCacheEntryFlags.DisableDistributedCache | HybridCacheEntryFlags.DisableLocalCacheWrite |
                HybridCacheEntryFlags.DisableUnderlyingData
        }, cancellationToken: ct);

    [ImmutableObject(true)]
    private sealed record Entry<T>(T Value, DateTimeOffset ConfirmedAt, DateTimeOffset RetryAt, DateTimeOffset HardDeadline);
}
