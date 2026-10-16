#if (LocalIdentity)
using Leistd.Lock.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace CompanyName.ProjectName.IntegrationTests.Fixtures;

/// <summary>固定并发交错的门：布防后只拦第一次经过的调用，放行前一直停在那里。</summary>
/// <remarks>
/// 用法：替身包住某个服务（<see cref="Wrap{TService}"/>），在要拦的那一步之前 <see cref="Arm"/>，
/// 等 <see cref="Entered"/> 后让另一边动手，再 <see cref="Release"/>。准备阶段经过同一个调用不受影响。
/// </remarks>
public sealed class InterleavingGate
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed;

    public Task Entered => _entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

    public void Arm() => Interlocked.Exchange(ref _armed, 1);

    public void Release() => _release.TrySetResult();

    public async Task PassAsync()
    {
        if (Interlocked.Exchange(ref _armed, 0) == 0)
            return;
        _entered.TrySetResult();
        // 用例在放行之前失败时不能把请求永远挂着（它可能持着锁）：等待有上限，到时照常往下走
        try
        {
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
        }
    }

    /// <summary>把已登记的 <typeparamref name="TService"/> 换成包了它的替身，生命周期不变。</summary>
    public static void Wrap<TService>(IServiceCollection services, Func<TService, TService> wrap) where TService : class
    {
        var descriptor = services.Last(x => x.ServiceType == typeof(TService));
        services.Remove(descriptor);
        services.Add(new ServiceDescriptor(typeof(TService), sp => wrap((TService)(
            descriptor.ImplementationInstance
            ?? descriptor.ImplementationFactory?.Invoke(sp)
            ?? ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!))), descriptor.Lifetime));
    }

    /// <summary>拦某个锁键的第一次获取：停在取锁之前，锁还没被占。</summary>
    public void GateLock(IServiceCollection services, Func<string, bool> key) =>
        Wrap<IDistributedLock>(services, inner => new LockGate(this, inner, key));

    private sealed class LockGate(InterleavingGate gate, IDistributedLock inner, Func<string, bool> key) : IDistributedLock
    {
        public async Task<ILockHandle> LockAsync(string name, CancellationToken cancellationToken = default)
        {
            if (key(name))
                await gate.PassAsync();
            return await inner.LockAsync(name, cancellationToken);
        }

        public Task<ILockHandle?> TryLockAsync(string name, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            inner.TryLockAsync(name, timeout, cancellationToken);
    }
}
#endif
