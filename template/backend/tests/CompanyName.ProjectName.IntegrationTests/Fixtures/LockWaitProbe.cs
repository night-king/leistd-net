#if (LocalIdentity)
using Leistd.Lock.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace CompanyName.ProjectName.IntegrationTests.Fixtures;

/// <summary>观察"另一个请求正在等某把锁"：布防后，目标键第一次取不到锁时发出信号，再进入真实等待。</summary>
/// <remarks>
/// 包住宿主实际注册的 <see cref="IDistributedLock"/>（<see cref="Install"/>），用例在要观察的请求发出前 <see cref="Arm"/>。
/// 判定只经锁接口：先 <c>TryLockAsync(key, TimeSpan.Zero)</c> 试一次，取不到才算在等；取到了说明没人持锁，照常返回句柄。
/// 准备阶段经过同一把锁不受影响。
/// </remarks>
public sealed class LockWaitProbe
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _key;

    /// <summary>把已登记的锁换成带观察的替身，生命周期不变。</summary>
    public void Install(IServiceCollection services) =>
        InterleavingGate.Wrap<IDistributedLock>(services, inner => new WaitSignallingLock(this, inner));

    /// <summary>从现在起观察 <paramref name="key"/>：第一次因它被占而等待时发出信号。</summary>
    public void Arm(string key) => Volatile.Write(ref _key, key);

    /// <summary>
    /// 断言 <paramref name="request"/> 正挡在布防的锁上：先等到"开始等锁"的信号（上限只防挂死），此时请求必然尚未完成。
    /// 请求不取这把锁、或取锁时没人持有，它会先跑完，断言立即失败，而不是靠等一段时间碰运气。
    /// </summary>
    public async Task AssertWaitingAsync(Task request)
    {
        var first = await Task.WhenAny(_waiting.Task, request).WaitAsync(Guard);
        Assert.True(first == _waiting.Task, "The request finished without waiting for the armed lock");
        Assert.False(request.IsCompleted, "The request completed while the armed lock was still held");
    }

    private bool Matches(string key) => !_waiting.Task.IsCompleted && Volatile.Read(ref _key) == key;

    private sealed class WaitSignallingLock(LockWaitProbe probe, IDistributedLock inner) : IDistributedLock
    {
        public async Task<ILockHandle> LockAsync(string key, CancellationToken cancellationToken = default)
        {
            if (!probe.Matches(key))
                return await inner.LockAsync(key, cancellationToken);

            var handle = await inner.TryLockAsync(key, TimeSpan.Zero, cancellationToken);
            if (handle is not null)
                return handle;

            // 信号发出时这次获取确实尚未完成：持锁方放行之前它不会返回
            probe._waiting.TrySetResult();
            return await inner.LockAsync(key, cancellationToken);
        }

        public Task<ILockHandle?> TryLockAsync(string key, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            inner.TryLockAsync(key, timeout, cancellationToken);
    }
}
#endif
