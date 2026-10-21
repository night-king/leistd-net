using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Leistd.Lock.Redis.Options;
using Leistd.Lock.Abstractions;

namespace Leistd.Lock.Redis;

/// <summary>通过 Redis 租约提供跨进程互斥。</summary>
public sealed class RedisDistributedLock(
    IConnectionMultiplexer connectionMultiplexer,
    IOptions<RedisLockOptions> options,
    ILogger<RedisDistributedLock> logger,
    TimeProvider? timeProvider = null) : IDistributedLock
{
    private readonly RedisLockOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<ILockHandle> LockAsync(string key, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handle = await TryAcquireAsync(key, cancellationToken);
            if (handle != null)
                return handle;
            await Task.Delay(_options.RetryInterval, _timeProvider, cancellationToken);
        }
    }

    /// <inheritdoc />
    public Task<ILockHandle?> TryLockAsync(string key, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // 负值是编程错误；无限等待应使用 LockAsync。
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        return TryAcquireWithRetryAsync(
            ct => TryAcquireAsync(key, ct),
            timeout,
            _options.RetryInterval,
            _timeProvider,
            () => logger.LogDebug("Failed to acquire lock [{Key}]: timeout", key),
            cancellationToken);
    }

    // 先尝试再判断超时，使零超时仍尝试一次并与内存实现一致。
    // 使用单调时间，避免墙钟调整改变等待期限。
    internal static async Task<ILockHandle?> TryAcquireWithRetryAsync(
        Func<CancellationToken, Task<ILockHandle?>> attempt,
        TimeSpan timeout,
        TimeSpan retryInterval,
        TimeProvider timeProvider,
        Action onTimeout,
        CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetTimestamp();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var handle = await attempt(cancellationToken);
            if (handle != null)
            {
                return handle;
            }

            var remaining = timeout - timeProvider.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var delay = retryInterval < remaining ? retryInterval : remaining;
            await Task.Delay(delay, timeProvider, cancellationToken);
        }

        onTimeout();
        return null;
    }

    private async Task<ILockHandle?> TryAcquireAsync(string key, CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N");
        var redisKey = Prefixed(key);
        var expiry = _options.Expiry;

        var acquired = await TakeAsync(redisKey, token, expiry);
        if (!acquired)
            return null;

        logger.LogDebug("Lock [{Key}] acquired", redisKey);
        return new RedisLockHandle(
            redisKey,
            expiry,
            e => ExtendAsync(redisKey, token, e),
            () => ReleaseAsync(redisKey, token),
            logger,
            _timeProvider);
    }

    private string Prefixed(string key) =>
        _options.KeyPrefix.Length == 0 ? key : _options.KeyPrefix + key;

    private async Task<bool> TakeAsync(string key, string token, TimeSpan expiry)
    {
        var db = connectionMultiplexer.GetDatabase();
        return await db.LockTakeAsync(key, token, expiry);
    }

    // 仅续期仍由当前令牌持有的锁，避免延长其他持有者的租约。
    private async Task<bool> ExtendAsync(string key, string token, TimeSpan expiry)
    {
        var db = connectionMultiplexer.GetDatabase();
        return await db.LockExtendAsync(key, token, expiry);
    }

    private async Task ReleaseAsync(string key, string token)
    {
        var db = connectionMultiplexer.GetDatabase();

        var released = await db.LockReleaseAsync(key, token);
        if (released)
            logger.LogDebug("Lock [{Key}] released", key);
        else
            logger.LogWarning("Failed to release lock [{Key}]: it has expired or is held by another owner", key);
    }
}
