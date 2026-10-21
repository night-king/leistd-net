using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Leistd.Lock.Memory.HostedServices;

/// <summary>定期回收长时间空闲的内存锁条目。</summary>
public sealed class MemoryLockCleanupHostedService : IHostedService, IDisposable
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxIdleTime = TimeSpan.FromMinutes(5);

    private readonly MemoryLocalLock _memoryLock;
    private readonly ILogger<MemoryLockCleanupHostedService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _lifecycleLock = new();

    private ITimer? _cleanupTimer;
    private Task? _stopTask;
    private bool _disposed;
    private int _stopping;

    /// <summary>创建清理后台服务，周期性回收无人等待的信号量条目。</summary>
    public MemoryLockCleanupHostedService(
        MemoryLocalLock memoryLock,
        ILogger<MemoryLockCleanupHostedService> logger)
        : this(memoryLock, logger, TimeProvider.System)
    {
    }

    internal MemoryLockCleanupHostedService(
        MemoryLocalLock memoryLock,
        ILogger<MemoryLockCleanupHostedService> logger,
        TimeProvider timeProvider)
    {
        _memoryLock = memoryLock;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopTask is not null)
                throw new InvalidOperationException("The memory lock cleanup service cannot be restarted after it has stopped.");

            _cleanupTimer ??= _timeProvider.CreateTimer(
                static state => ((MemoryLockCleanupHostedService)state!).RunCleanup(),
                this,
                CleanupInterval,
                CleanupInterval);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => StopCore();

    internal int CleanupOnce()
    {
        if (Volatile.Read(ref _stopping) != 0) return 0;

        _logger.LogTrace("Cleaning up expired semaphore locks...");
        var removedCount = 0;
        var now = _memoryLock.GetUtcNow();
        foreach (var (key, entry) in _memoryLock.Semaphores)
        {
            if (Volatile.Read(ref _stopping) != 0) break;

            if (entry.TryRetire(now, MaxIdleTime) && _memoryLock.TryRemove(key, entry))
            {
                removedCount++;
                _logger.LogTrace("Cleaned up expired semaphore lock [{Key}]", key);
            }
        }
        _logger.LogTrace("Expired semaphore lock cleanup completed");
        return removedCount;
    }

    private void RunCleanup()
    {
        try
        {
            CleanupOnce();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to clean up expired semaphore locks");
        }
    }

    private Task StopCore()
    {
        Interlocked.Exchange(ref _stopping, 1);

        lock (_lifecycleLock)
        {
            if (_stopTask is not null) return _stopTask;

            var timer = _cleanupTimer;
            _cleanupTimer = null;
            _stopTask = timer is null
                ? Task.CompletedTask
                : timer.DisposeAsync().AsTask();
            return _stopTask;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            _disposed = true;
        }

        // 计时器在线程池回调，同步等待可确保依赖的单例在停止完成前仍存活。
        StopCore().GetAwaiter().GetResult();
    }
}
