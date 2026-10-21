using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Leistd.Lock.Abstractions;

namespace Leistd.Lock.Memory;

/// <summary>使用按键隔离的进程内信号量提供互斥。</summary>
public sealed class MemoryLocalLock : ILocalLock, IDistributedLock, IDisposable
{
    private readonly ILogger<MemoryLocalLock> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, SemaphoreEntry> _semaphores = new();

    internal ConcurrentDictionary<string, SemaphoreEntry> Semaphores => _semaphores;

    /// <summary>创建进程内锁实现。</summary>
    public MemoryLocalLock(ILogger<MemoryLocalLock> logger)
        : this(logger, TimeProvider.System)
    {
    }

    internal MemoryLocalLock(ILogger<MemoryLocalLock> logger, TimeProvider timeProvider)
    {
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<ILockHandle> LockAsync(string key, CancellationToken cancellationToken = default)
    {
        _logger.LogTrace("Acquiring lock [{Key}]...", key);
        var entry = AcquireEntryLease(key);
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            entry.AbandonLease();
            throw;
        }

        _logger.LogTrace("Lock [{Key}] acquired", key);
        return new MemoryLockHandle(key, entry, this);
    }

    /// <inheritdoc />
    public async Task<ILockHandle?> TryLockAsync(string key, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        _logger.LogTrace("Trying to acquire lock [{Key}]...", key);
        var entry = AcquireEntryLease(key);
        bool acquired;
        try
        {
            acquired = await entry.Semaphore.WaitAsync(timeout, cancellationToken);
        }
        catch
        {
            entry.AbandonLease();
            throw;
        }

        if (!acquired)
        {
            entry.AbandonLease();
            _logger.LogDebug("Failed to acquire lock [{Key}]: timeout", key);
            return null;
        }

        _logger.LogTrace("Lock [{Key}] acquired on try", key);
        return new MemoryLockHandle(key, entry, this);
    }

    internal void Release(string key, SemaphoreEntry entry)
    {
        if (entry.ReleaseLease(_timeProvider.GetUtcNow()))
            _logger.LogTrace("Lock [{Key}] released", key);
    }

    internal bool TryRemove(string key, SemaphoreEntry entry)
    {
        if (!_semaphores.TryRemove(new KeyValuePair<string, SemaphoreEntry>(key, entry)))
            return false;

        entry.Dispose();
        return true;
    }

    internal DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();

    private SemaphoreEntry AcquireEntryLease(string key)
    {
        while (true)
        {
            var entry = _semaphores.GetOrAdd(key, _ => new SemaphoreEntry(_timeProvider.GetUtcNow()));
            if (entry.TryAcquireLease())
                return entry;

            // 条目已退役但尚未从字典移除时仍可能短暂可见，移除后重取。
            TryRemove(key, entry);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var (key, entry) in _semaphores)
            TryRemove(key, entry);
    }


}
