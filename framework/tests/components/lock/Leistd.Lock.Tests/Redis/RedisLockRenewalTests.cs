using Leistd.Lock.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using Xunit;
using Leistd.Lock.Abstractions;

namespace Leistd.Lock.Tests.Redis;

/// <summary>Redis 锁句柄的续期与失锁行为。</summary>
/// <remarks>
/// 不续期的租约只保证"拿到锁的那一刻是独占的"：临界区一旦超过租约时长，锁自动过期，
/// 另一个实例合法进入，而当前进程仍在写。这条路径没有测试就等于没有。
///
/// 直接构造句柄并注入续期/释放委托，而不是给 StackExchange.Redis 的 <c>IDatabase</c> 造 mock
/// （两百余个成员），也不为测试解封生产类型——公共 API 不该为测试留扩展点。
///
/// 时间走 <see cref="FakeTimeProvider"/>（句柄的构造参数本就为此留了口子）。用真实时钟
/// 睡一段再断言"续了几次"，赌的是"这段墙上时间里续期循环拿得到调度"——CI 上多个测试
/// 程序集并行、线程池被挤占时这个前提不成立，用例就会毫无规律地红。
/// </remarks>
public class RedisLockRenewalTests
{
    private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(30);

    // 续期间隔是租约的三分之一（见 RedisLockHandle）。推进时间按这个步长走。
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task The_lease_is_renewed_while_the_handle_is_held()
    {
        var probe = new HandleProbe();

        await using (probe.CreateHandle())
        {
            // 连续两轮续期：不续期的实现在这里等不到。
            await probe.AdvanceUntilAsync(() => probe.ExtendCount >= 2, "持锁期间应当持续续期。");
        }

        Assert.Equal(1, probe.ReleaseCount);
    }

    [Fact]
    public async Task Losing_the_lease_cancels_the_handle_and_skips_release()
    {
        var probe = new HandleProbe { ExtendResult = false };

        var handle = probe.CreateHandle();

        // 续期返回 false 意味着 key 已不再持有本次的 token——锁现在属于别人。
        await probe.AdvanceUntilAsync(
            () => handle.LockLost.IsCancellationRequested,
            "持锁资格失效后 LockLost 应当被取消。");
        await handle.DisposeAsync();

        // 此时删除 key 等于把新持有者的临界区放开，必须跳过。
        Assert.Equal(0, probe.ReleaseCount);
    }

    [Fact]
    public async Task A_failing_renewal_channel_also_counts_as_losing_the_lease()
    {
        var probe = new HandleProbe { ExtendThrows = true };

        var handle = probe.CreateHandle();

        // 连接断开时无法再证明自己持有锁，与续期失败同等对待。
        await probe.AdvanceUntilAsync(
            () => handle.LockLost.IsCancellationRequested,
            "续期通道故障后 LockLost 应当被取消。");
        await handle.DisposeAsync();

        Assert.Equal(0, probe.ReleaseCount);
    }

    [Fact]
    public async Task Disposing_stops_the_renewal_loop()
    {
        var probe = new HandleProbe();

        await using (probe.CreateHandle())
        {
            await probe.AdvanceUntilAsync(() => probe.ExtendCount >= 1, "持锁期间应当续期。");
        }

        var afterDispose = probe.ExtendCount;
        // 再推进几个租约周期：还在跑的循环一定会在这里留下痕迹。
        await probe.AdvanceAsync(Interval * 9);

        // 释放之后仍在续期，等于持续给一把已经不属于自己的锁续命。
        Assert.Equal(afterDispose, probe.ExtendCount);
    }

    [Fact]
    public async Task Disposing_twice_is_a_no_op()
    {
        var probe = new HandleProbe();
        var handle = probe.CreateHandle();

        await handle.DisposeAsync();
        await handle.DisposeAsync();

        // 与内存句柄同一契约：重复释放既不抛异常，也不重复删 key。
        Assert.Equal(1, probe.ReleaseCount);
    }

    [Fact]
    public async Task Concurrent_disposal_releases_once()
    {
        var probe = new HandleProbe();
        var handle = probe.CreateHandle();

        await Task.WhenAll(
            Task.Run(async () => await handle.DisposeAsync()),
            Task.Run(async () => await handle.DisposeAsync()));

        Assert.Equal(1, probe.ReleaseCount);
    }

    private sealed class HandleProbe
    {
        private readonly FakeTimeProvider time = new();
        private readonly SemaphoreSlim extended = new(0);
        private int extendCount;
        private int releaseCount;

        public bool ExtendResult { get; init; } = true;

        public bool ExtendThrows { get; init; }

        public int ExtendCount => Volatile.Read(ref extendCount);

        public int ReleaseCount => Volatile.Read(ref releaseCount);

        public ILockHandle CreateHandle()
            => new RedisLockHandle("k", Expiry, ExtendAsync, ReleaseAsync, NullLogger.Instance, time);

        /// <summary>按续期间隔推进假时钟，直到条件成立。</summary>
        /// <remarks>
        /// 一次推进到位是不行的：续期循环每轮重新注册一次定时器，时间一次跨过多个周期，
        /// 也只会触发一次续期。所以按间隔逐步推进。
        /// <para>
        /// 每步之后等续期调用信号，超时就再推一步：<c>Advance</c> 只保证 <c>Task.Delay</c> 完成，
        /// 不保证续期循环已经跑完、登记了下一轮延时；在下一轮登记之前推进的时间不会被它计入。
        /// 不按让出线程的次数算——调度紧张时，几百次让出也可能在循环跑一轮之前用完。
        /// 信号只说明续期委托被调用过，结果仍由 <paramref name="condition"/> 判断；
        /// 总时限只用来把"续期逻辑被改坏"变成失败而不是挂起。
        /// </para>
        /// </remarks>
        public async Task AdvanceUntilAsync(Func<bool> condition, string because)
        {
            var deadline = TimeProvider.System.GetTimestamp() + TimeProvider.System.TimestampFrequency * 10;
            while (!condition() && TimeProvider.System.GetTimestamp() < deadline)
            {
                time.Advance(Interval);
                await extended.WaitAsync(TimeSpan.FromMilliseconds(50));
            }

            Assert.True(condition(), because);
        }

        /// <summary>按间隔逐步推进假时钟走完整段时长，不带条件。</summary>
        public async Task AdvanceAsync(TimeSpan total)
        {
            for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += Interval)
            {
                time.Advance(Interval);
                await Task.Yield();
            }
        }

        private Task<bool> ExtendAsync(TimeSpan expiry)
        {
            Interlocked.Increment(ref extendCount);
            extended.Release();

            return ExtendThrows
                ? Task.FromException<bool>(new RedisConnectionException(
                    ConnectionFailureType.SocketFailure,
                    CommandFlags.None,
                    "boom",
                    innerException: null,
                    CommandStatus.Unknown))
                : Task.FromResult(ExtendResult);
        }

        private Task ReleaseAsync()
        {
            Interlocked.Increment(ref releaseCount);
            return Task.CompletedTask;
        }
    }
}
