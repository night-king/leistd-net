namespace Leistd.Lock.Abstractions;

/// <summary>按键互斥的异步锁。</summary>
public interface ILock
{
    /// <summary>等待加锁，直到获取成功或取消。</summary>
    Task<ILockHandle> LockAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>在给定时长内尝试加锁；超时返回 <see langword="null"/>，调用方可自行降级。</summary>
    /// <remarks>
    /// <paramref name="timeout"/> 必须非负，负值抛 <see cref="ArgumentOutOfRangeException"/>；
    /// <see cref="TimeSpan.Zero"/> 表示"试一次、不等待"。
    /// 无限等待请用 <see cref="LockAsync"/>，不要传 <see cref="Timeout.InfiniteTimeSpan"/>。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> 为负。</exception>
    /// <example>
    /// <code>
    /// await using var handle = await distributedLock.TryLockAsync("order:1001", TimeSpan.FromSeconds(3), ct);
    /// if (handle is null) return;
    ///
    /// using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, handle.LockLost);
    /// await ProcessAsync(linked.Token);
    /// </code>
    /// </example>
    Task<ILockHandle?> TryLockAsync(string key, TimeSpan timeout, CancellationToken cancellationToken = default);
}
