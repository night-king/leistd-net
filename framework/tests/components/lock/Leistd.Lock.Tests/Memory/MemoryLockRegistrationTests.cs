using Leistd.Lock.Abstractions;
using Leistd.Lock.Memory;
using Leistd.Lock.Memory.HostedServices;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Leistd.Lock.Tests.Memory;

/// <summary><c>AddMemoryLocalLock</c> 的注册面：全部单例，重复调用不多挂兜底与清理服务。</summary>
/// <remarks>
/// 锁表在实例里：任一抽象被登记成非单例，两处拿到的就是两张锁表，互斥当场不成立且不报错。
/// 重复调用若再追加一条兜底，<see cref="IDistributedLock"/> 会出现两条描述符，Redis 入口只移除其中一条。
/// </remarks>
public sealed class MemoryLockRegistrationTests
{
    [Fact]
    public void Registration_registers_every_lock_abstraction_as_a_singleton()
    {
        var services = new ServiceCollection();

        services.AddMemoryLocalLock();

        services.AssertSingle<MemoryLocalLock>(ServiceLifetime.Singleton);
        services.AssertSingle<ILocalLock>(ServiceLifetime.Singleton);
        services.AssertSingle<IDistributedLock>(ServiceLifetime.Singleton);
        Assert.Single(services, d => d.ServiceType == typeof(IHostedService)
                                     && d.ImplementationType == typeof(MemoryLockCleanupHostedService));
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddMemoryLocalLock());
}
