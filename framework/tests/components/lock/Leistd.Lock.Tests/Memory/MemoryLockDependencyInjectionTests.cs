using Leistd.Lock.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Leistd.Lock.Abstractions;

namespace Leistd.Lock.Tests.Memory;

public sealed class MemoryLockDependencyInjectionTests
{
    private const string Key = "shared-service-instance";

    [Fact]
    public void AllLockAbstractionsResolveToTheSameSingleton()
    {
        using var serviceProvider = CreateServiceProvider();

        var implementation = serviceProvider.GetRequiredService<MemoryLocalLock>();

        Assert.Same(implementation, serviceProvider.GetRequiredService<ILocalLock>());
        Assert.Same(implementation, serviceProvider.GetRequiredService<IDistributedLock>());
        // ILock 只是两者的基接口，不作为服务注册：注入它看不出拿到的是本地锁还是跨副本锁
        Assert.Null(serviceProvider.GetService<ILock>());
    }

    [Fact]
    public async Task DifferentLockAbstractionsShareKeyMutualExclusion()
    {
        using var serviceProvider = CreateServiceProvider();
        var localLock = serviceProvider.GetRequiredService<ILocalLock>();
        var distributedLock = serviceProvider.GetRequiredService<IDistributedLock>();

        await using var heldLock = await localLock.LockAsync(Key);

        Assert.Null(await distributedLock.TryLockAsync(Key, TimeSpan.Zero));
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<MemoryLocalLock>>(NullLogger<MemoryLocalLock>.Instance);
        services.AddMemoryLocalLock();
        return services.BuildServiceProvider();
    }

}
