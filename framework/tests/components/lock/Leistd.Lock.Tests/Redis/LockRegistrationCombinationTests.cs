using Leistd.Lock.Abstractions;
using Leistd.Lock.Memory;
using Leistd.Lock.Redis;
using Leistd.Lock.Registration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Lock.Tests.Redis;

/// <summary>
/// 内存锁与 Redis 锁可以共存：跨副本互斥用 Redis，进程内互斥仍可注入 <see cref="ILocalLock"/>，结果与注册顺序无关；
/// 宿主自己注册的 <see cref="IDistributedLock"/> 不被覆盖。
/// </summary>
/// <remarks>Redis 实现解析时会连接 Redis，这里按注册描述符断言，不实际解析。</remarks>
public sealed class LockRegistrationCombinationTests
{
    [Fact]
    public void Redis_replaces_the_in_process_fallback_registered_before_it()
    {
        var services = new ServiceCollection().AddMemoryLocalLock().AddRedisDistributedLock("localhost:6379");

        AssertServedByRedis(services);
    }

    [Fact]
    public void The_in_process_lock_registered_after_Redis_does_not_take_over()
    {
        var services = new ServiceCollection().AddRedisDistributedLock("localhost:6379").AddMemoryLocalLock();

        AssertServedByRedis(services);
    }

    [Fact]
    public void A_host_lock_is_kept_by_the_in_process_registration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDistributedLock, HostLock>();
        services.AddMemoryLocalLock();

        Assert.Equal(typeof(HostLock), Assert.Single(services, d => d.ServiceType == typeof(IDistributedLock)).ImplementationType);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(InProcessDistributedLockMarker));
    }

    [Fact]
    public void A_host_lock_is_kept_by_the_Redis_registration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDistributedLock, HostLock>();
        services.AddRedisDistributedLock("localhost:6379");

        Assert.Equal(typeof(HostLock), Assert.Single(services, d => d.ServiceType == typeof(IDistributedLock)).ImplementationType);
    }

    // 宿主在内存兜底之后才注册自己的锁：Redis 只能移除兜底那一条，不能连宿主实现一起删
    [Fact]
    public void A_host_lock_registered_after_the_fallback_is_kept_by_the_Redis_registration()
    {
        var services = new ServiceCollection();
        services.AddMemoryLocalLock();
        services.AddSingleton<IDistributedLock, HostLock>();
        services.AddRedisDistributedLock("localhost:6379");

        Assert.Equal(typeof(HostLock), Assert.Single(services, d => d.ServiceType == typeof(IDistributedLock)).ImplementationType);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(InProcessDistributedLockMarker));
    }

    // 具名锁不是默认实现：它不能阻止内存锁兜底默认的 IDistributedLock
    [Fact]
    public void A_keyed_host_lock_does_not_suppress_the_default_fallback()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IDistributedLock, HostLock>("imports");
        services.AddMemoryLocalLock();

        Assert.Single(services, d => d.ServiceType == typeof(IDistributedLock) && !d.IsKeyedService);
        Assert.Contains(services, d => d.ServiceType == typeof(InProcessDistributedLockMarker));
    }

    private static void AssertServedByRedis(IServiceCollection services)
    {
        // 兜底已让位：只剩 Redis 那一条转发注册，标记也一并移除
        var distributed = Assert.Single(services, d => d.ServiceType == typeof(IDistributedLock));
        Assert.NotNull(distributed.ImplementationFactory);
        Assert.Contains(services, d => d.ServiceType == typeof(RedisDistributedLock));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(InProcessDistributedLockMarker));
        // 进程内锁仍可单独注入
        Assert.Contains(services, d => d.ServiceType == typeof(ILocalLock));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ILock));
    }

    private sealed class HostLock : IDistributedLock
    {
        public Task<ILockHandle> LockAsync(string key, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ILockHandle?> TryLockAsync(string key, TimeSpan timeout, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
