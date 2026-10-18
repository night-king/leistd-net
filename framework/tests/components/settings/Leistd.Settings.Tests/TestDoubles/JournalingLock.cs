using Leistd.Lock.Abstractions;

namespace Leistd.Settings.Tests.TestDoubles;

// 包住真实的进程内锁，记下取锁、等待与释放的先后。
// Waiting 在“锁已被他人持有、本次调用即将等待”时触发：先试一次不等待，失败后才发信号再进入等待，
// 因此信号发出时这次获取确实尚未完成，而不是按调用次数推断。
internal sealed class JournalingLock(IDistributedLock inner, Action<string>? journal = null) : IDistributedLock
{
    private readonly CancellationTokenSource _lockLost = new();

    public event Action<string>? Waiting;

    // 模拟租约续期失败：之后发出的句柄与已发出的句柄共用这一个令牌
    public void LoseLease() => _lockLost.Cancel();

    public async Task<ILockHandle> LockAsync(string key, CancellationToken cancellationToken = default)
    {
        var handle = await inner.TryLockAsync(key, TimeSpan.Zero, cancellationToken);
        if (handle is null)
        {
            journal?.Invoke($"lock-waiting:{key}");
            Waiting?.Invoke(key);
            handle = await inner.LockAsync(key, cancellationToken);
        }

        journal?.Invoke($"lock-acquired:{key}");
        return new Handle(
            handle,
            _lockLost.Token,
            () => journal?.Invoke($"lock-release-start:{key}"),
            () => journal?.Invoke($"lock-released:{key}"));
    }

    public Task<ILockHandle?> TryLockAsync(string key, TimeSpan timeout, CancellationToken cancellationToken = default)
        => inner.TryLockAsync(key, timeout, cancellationToken);

    // releasing 在真正解锁之前记录，是"其他持有者进入之前"的确定先后；released 只表示本方已返回，
    // 等待者可能先于它被唤醒并记录取锁
    private sealed class Handle(ILockHandle inner, CancellationToken lockLost, Action releasing, Action released) : ILockHandle
    {
        private int _disposed;

        public CancellationToken LockLost => lockLost;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            releasing();
            await inner.DisposeAsync();
            released();
        }
    }
}
