using Microsoft.Extensions.Logging;
using Leistd.Lock.Abstractions;

namespace Leistd.Lock.Redis;

// 后台续约维持临界区互斥；续约失败必须通知持有者中止操作。
internal sealed class RedisLockHandle : ILockHandle
{
    private readonly string key;
    private readonly Func<TimeSpan, Task<bool>> extend;
    private readonly Func<Task> release;
    private readonly ILogger logger;
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource lockLost = new();
    private readonly CancellationTokenSource renewalStopped = new();
    private readonly Task renewalLoop;
    private int disposed;

    /// <remarks>续期与释放以委托传入，测试可直接构造句柄验证续期与失锁行为。</remarks>
    public RedisLockHandle(
        string key,
        TimeSpan expiry,
        Func<TimeSpan, Task<bool>> extend,
        Func<Task> release,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        this.key = key;
        this.extend = extend;
        this.release = release;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;

        // 续期间隔取租约的三分之一：允许连续两次失败仍来得及在过期前放弃。
        var interval = TimeSpan.FromMilliseconds(Math.Max(expiry.TotalMilliseconds / 3, 1));
        renewalLoop = RenewAsync(interval, expiry);
    }

    public CancellationToken LockLost => lockLost.Token;

    public async ValueTask DisposeAsync()
    {
        // 与内存句柄保持同一契约：重复释放是无操作，而不是在已释放的 CTS 上抛异常。
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        await renewalStopped.CancelAsync();

        try
        {
            await renewalLoop;
        }
        catch (OperationCanceledException)
        {
        }

        renewalStopped.Dispose();

        try
        {
            // 已经失去锁时不再删除：那把锁现在属于别人，删掉等于把别人的临界区放开。
            if (!lockLost.IsCancellationRequested)
            {
                await release();
            }
        }
        finally
        {
            lockLost.Dispose();
        }
    }

    private async Task RenewAsync(TimeSpan interval, TimeSpan expiry)
    {
        try
        {
            while (!renewalStopped.IsCancellationRequested)
            {
                await Task.Delay(interval, timeProvider, renewalStopped.Token);

                if (!await extend(expiry))
                {
                    logger.LogWarning("Failed to renew lock [{Key}]; lock ownership has been lost", key);
                    await lockLost.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 句柄被释放，正常退出。
        }
        catch (Exception exception)
        {
            // 续期通道本身出问题（连接断开等）同样意味着无法再证明自己持有锁。
            logger.LogWarning(exception, "Error while renewing lock [{Key}]; lock ownership is treated as lost", key);
            await lockLost.CancelAsync();
        }
    }
}
