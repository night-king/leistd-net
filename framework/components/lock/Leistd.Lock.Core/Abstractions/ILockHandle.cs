namespace Leistd.Lock.Abstractions;

/// <summary>锁句柄，释放时归还锁；重复释放无操作。</summary>
public interface ILockHandle : IAsyncDisposable
{
    /// <summary>持锁资格失效时被取消。</summary>
    /// <remarks>
    /// 基于租约的实现（如 Redis）续期失败后，另一个实例可以合法进入临界区；长事务、数据迁移等临界区应把本令牌
    /// 与自己的 <see cref="CancellationToken"/> 关联。进程内实现没有租约，本令牌在释放前永不取消。
    /// </remarks>
    CancellationToken LockLost { get; }
}
