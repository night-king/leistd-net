using StackExchange.Redis;
using Xunit;

namespace Leistd.Lock.Tests.Redis;

/// <summary>Redis 租约本身的语义：令牌归属、过期、键前缀。</summary>
/// <remarks>
/// 这些是基于租约的实现独有的失效方式，进程内锁不存在，因此不属于共享契约。
/// 它们全都属于"出问题不报错、只让互斥静默失效"那一类：
/// 释放了别人的锁、续期了别人的租约，调用方都收不到任何信号。
/// </remarks>
public sealed class RedisLeaseTests
{
    private static IDatabase Db => RedisServer.Connection.GetDatabase();

    // 锁键必须带上配置的前缀：宿主用它把不同应用/环境隔在同一个 Redis 实例上，
    // 前缀没生效意味着两套系统会互相抢同一把锁。
    [SkippableFact]
    public async Task Key_prefix_is_applied_to_the_redis_key()
    {
        Skip.IfNot(RedisServer.IsAvailable, RedisServer.SkipReason);

        var prefix = $"leistd-test:{Guid.NewGuid():N}:";
        var sut = RedisDistributedLockContractTests.NewLock(o => o.KeyPrefix = prefix);
        const string Key = "order-1001";

        await using var handle = await sut.LockAsync(Key);

        Assert.True(await Db.KeyExistsAsync(prefix + Key));
        Assert.False(await Db.KeyExistsAsync(Key));
    }

    // 释放只认令牌：另一个持有者（或过期后接手的人）不得因为知道键名就能把锁放掉。
    // 这条失效时，A 的释放会解开 B 正持有的锁，B 毫不知情地继续写。
    [SkippableFact]
    public async Task Releasing_with_a_foreign_token_does_not_free_the_lock()
    {
        Skip.IfNot(RedisServer.IsAvailable, RedisServer.SkipReason);

        var prefix = $"leistd-test:{Guid.NewGuid():N}:";
        var sut = RedisDistributedLockContractTests.NewLock(o => o.KeyPrefix = prefix);
        const string Key = "order-2002";

        await using var held = await sut.LockAsync(Key);

        var stolen = await Db.LockReleaseAsync(prefix + Key, "someone-elses-token");

        Assert.False(stolen);
        Assert.True(await Db.KeyExistsAsync(prefix + Key));
        Assert.Null(await sut.TryLockAsync(Key, TimeSpan.Zero));
    }

    // 续期同样只认令牌，否则一个已经失去锁的进程会把当前持有者的租约越续越长。
    [SkippableFact]
    public async Task Extending_with_a_foreign_token_is_refused()
    {
        Skip.IfNot(RedisServer.IsAvailable, RedisServer.SkipReason);

        var prefix = $"leistd-test:{Guid.NewGuid():N}:";
        var sut = RedisDistributedLockContractTests.NewLock(o => o.KeyPrefix = prefix);
        const string Key = "order-3003";

        await using var held = await sut.LockAsync(Key);

        Assert.False(await Db.LockExtendAsync(prefix + Key, "someone-elses-token", TimeSpan.FromMinutes(5)));
    }

    // 释放之后键必须真的消失，而不是留一个空租约把这个键永久占住。
    [SkippableFact]
    public async Task Releasing_removes_the_redis_key()
    {
        Skip.IfNot(RedisServer.IsAvailable, RedisServer.SkipReason);

        var prefix = $"leistd-test:{Guid.NewGuid():N}:";
        var sut = RedisDistributedLockContractTests.NewLock(o => o.KeyPrefix = prefix);
        const string Key = "order-4004";

        await (await sut.LockAsync(Key)).DisposeAsync();

        Assert.False(await Db.KeyExistsAsync(prefix + Key));
    }

    // 租约带 TTL：进程崩溃不能让锁永远留在 Redis 里。
    // 没有 TTL 时症状是"某个键从此再也拿不到"，且不会有任何报错。
    [SkippableFact]
    public async Task The_lease_carries_an_expiry_so_a_crashed_holder_cannot_deadlock_the_key()
    {
        Skip.IfNot(RedisServer.IsAvailable, RedisServer.SkipReason);

        var prefix = $"leistd-test:{Guid.NewGuid():N}:";
        var expiry = TimeSpan.FromSeconds(30);
        var sut = RedisDistributedLockContractTests.NewLock(o =>
        {
            o.KeyPrefix = prefix;
            o.Expiry = expiry;
        });

        await using var held = await sut.LockAsync("order-5005");

        var ttl = await Db.KeyTimeToLiveAsync(prefix + "order-5005");

        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.Zero, expiry);
    }

    // 租约到期后锁自动可再取——崩溃恢复靠的就是这条。
    [SkippableFact]
    public async Task An_expired_lease_lets_another_caller_acquire_the_key()
    {
        Skip.IfNot(RedisServer.IsAvailable, RedisServer.SkipReason);

        // 租约用默认长度，不缩短：句柄每隔租约的三分之一续期一次，租约短到 1 秒时，
        // 测试机调度抖动可能让续期迟到、键提前过期，下一行"仍被占用"的断言就会偶发失败。
        // 到期本身用删键模拟，租约长短与这里要验证的事无关。
        var prefix = $"leistd-test:{Guid.NewGuid():N}:";
        var sut = RedisDistributedLockContractTests.NewLock(o => o.KeyPrefix = prefix);
        const string Key = "order-6006";

        var abandoned = await sut.LockAsync(Key);          // 刻意不释放：模拟持有者崩溃
        Assert.Null(await sut.TryLockAsync(Key, TimeSpan.Zero));

        // 直接把键删掉等价于"租约到期"，不必真等租约过去——
        // TTL 存在性已由上一条断言，这里要验的是"键消失后能立刻再取"
        await Db.KeyDeleteAsync(prefix + Key);

        await using var reacquired = await sut.TryLockAsync(Key, TimeSpan.Zero);
        Assert.NotNull(reacquired);

        await abandoned.DisposeAsync();
    }
}
