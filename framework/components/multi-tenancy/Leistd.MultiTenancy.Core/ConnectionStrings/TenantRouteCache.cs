using System.ComponentModel;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace Leistd.MultiTenancy.ConnectionStrings;

// 路由与物理库清单共用进程内的有界旧值；HybridCache 只负责单飞，不使用请求作用域保存状态。
internal sealed class TenantRouteCache(HybridCache cache, TimeProvider clock, ILogger logger)
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private static readonly HybridCacheEntryOptions InFlightOptions = new()
    {
        Flags = HybridCacheEntryFlags.DisableDistributedCache | HybridCacheEntryFlags.DisableLocalCacheWrite
    };

    public async ValueTask<T> GetAsync<T>(string key, TimeSpan freshFor, TimeSpan maxStaleness,
        Func<CancellationToken, ValueTask<T>> fetch, Func<Exception, bool> unavailable, CancellationToken cancellationToken)
    {
        var entry = await ReadAsync<T>(key, cancellationToken);
        if (entry is not null && clock.GetUtcNow() < entry.RetryAt) return entry.Value;
        var pending = cache.GetOrCreateAsync(key + ":fetch", async ct =>
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
                var previous = await UseStaleAsync<T>(key, freshFor, false, ct);
                if (previous is null) throw;
                return previous.Value;
            }
            var ttl = result.HardDeadline - clock.GetUtcNow();
            if (ttl > TimeSpan.Zero)
                await cache.SetAsync(key, result, new HybridCacheEntryOptions
                {
                    Flags = HybridCacheEntryFlags.DisableDistributedCache,
                    Expiration = ttl, LocalCacheExpiration = ttl
                }, cancellationToken: ct);
            return result.Value;
        }, InFlightOptions, cancellationToken: cancellationToken).AsTask();
        if (entry is null) return await pending;
        try
        {
            return await pending.WaitAsync(WaitBudget, clock, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // 回源自身的超时仍按原结果传播；只有等待预算耗尽才回落。
            if (pending.IsCompleted) return await pending;
            _ = pending.ContinueWith(static completed => { _ = completed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();
            var previous = await UseStaleAsync<T>(key, freshFor, true, cancellationToken);
            return previous is null ? await pending : previous.Value;
        }
    }

    // 共享节流转换，避免同键的多个超时等待者各自覆盖 RetryAt、重复记日志；不缓存这个协调结果。
    private ValueTask<Entry<T>?> UseStaleAsync<T>(string key, TimeSpan freshFor, bool timedOut, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync<Entry<T>?>(key + ":stale", async ct =>
        {
            var previous = await ReadAsync<T>(key, ct);
            var now = clock.GetUtcNow();
            if (previous is null || now >= previous.HardDeadline) return null;
            if (timedOut && now < previous.RetryAt) return previous;
            var retry = now + RetryDelay;
            var result = previous with { RetryAt = retry < previous.HardDeadline ? retry : previous.HardDeadline };
            await cache.SetAsync(key, result, new HybridCacheEntryOptions
            {
                Flags = HybridCacheEntryFlags.DisableDistributedCache,
                Expiration = result.HardDeadline - now, LocalCacheExpiration = result.HardDeadline - now
            }, cancellationToken: ct);
            if (now >= previous.RetryAt)
                logger.LogWarning("Control-plane dependency {Dependency} exceeded its wait budget or is unavailable; using state stale for {Staleness} while validation may continue.",
                    key, now - (previous.ConfirmedAt + freshFor));
            return result;
        }, InFlightOptions, cancellationToken: cancellationToken);

    private ValueTask<Entry<T>?> ReadAsync<T>(string key, CancellationToken ct) =>
        cache.GetOrCreateAsync<Entry<T>?>(key, static _ => ValueTask.FromResult<Entry<T>?>(null), new HybridCacheEntryOptions
        {
            Flags = HybridCacheEntryFlags.DisableDistributedCache | HybridCacheEntryFlags.DisableLocalCacheWrite |
                HybridCacheEntryFlags.DisableUnderlyingData
        }, cancellationToken: ct);

    [ImmutableObject(true)]
    private sealed record Entry<T>(T Value, DateTimeOffset ConfirmedAt, DateTimeOffset RetryAt, DateTimeOffset HardDeadline);
}
