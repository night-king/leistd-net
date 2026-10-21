using Leistd.Lock.Abstractions;

namespace Leistd.Lock.Memory;

// 内存锁句柄，释放时归还信号量
internal sealed class MemoryLockHandle(
    string key,
    SemaphoreEntry entry,
    MemoryLocalLock owner) : ILockHandle
{
    private int _disposed;

    /// <summary>进程内互斥没有租约，持锁资格不会在释放前失效。</summary>
    public CancellationToken LockLost => CancellationToken.None;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            owner.Release(key, entry);

        return ValueTask.CompletedTask;
    }
}
