using Leistd.Lock.Abstractions;
using Xunit;

// SkippableFact 在没有跳过时与普通 Fact 行为一致；
// 内存派生永不跳过，Redis 派生在没有服务时跳过。

namespace Leistd.Lock.Tests.Contracts;

/// <summary>任何 <see cref="ILock"/> 实现都必须兑现的行为。</summary>
/// <remarks>
/// <para><b>新增实现时必须派生本类。</b>这不是形式要求——契约很容易在实现间漂移：
/// 例如 <c>TryLockAsync</c> 写成 <c>while (now &lt; deadline)</c> 时，
/// <see cref="TimeSpan.Zero"/> 使循环一次都不进，于是同一个 <see cref="ILock"/> 在内存与 Redis 上
/// 给出不同行为，而按接口编程的调用方完全看不出来。靠人记得"两边都改"挡不住这类问题。</para>
/// <para>只断言接口承诺的部分。租约、续期、清理这些是实现自己的话题，留在各实现的用例里。</para>
/// </remarks>
public abstract class LockContractTests
{
    /// <summary>构造被测实现。同一个实例内的键必须互斥。</summary>
    /// <remarks>需要外部服务的实现在这里调用 <c>Skip.IfNot(...)</c>——没有服务时整类跳过。</remarks>
    protected abstract ILock CreateLock();

    private static string NewKey() => $"key-{Guid.NewGuid():N}";

    [SkippableFact]
    public async Task Lock_returns_a_handle_for_a_free_key()
    {
        await using var handle = await CreateLock().LockAsync(NewKey());

        Assert.NotNull(handle);
    }

    [SkippableFact]
    public async Task Try_lock_succeeds_on_a_free_key()
    {
        await using var handle = await CreateLock().TryLockAsync(NewKey(), TimeSpan.FromSeconds(1));

        Assert.NotNull(handle);
    }

    // 零超时的语义是"试一次、不等待"，不是"不试"。漂移就发生在这里。
    [SkippableFact]
    public async Task Zero_timeout_still_attempts_once()
    {
        await using var handle = await CreateLock().TryLockAsync(NewKey(), TimeSpan.Zero);

        Assert.NotNull(handle);
    }

    [SkippableFact]
    public async Task Zero_timeout_on_a_held_key_returns_null_instead_of_waiting()
    {
        var sut = CreateLock();
        var key = NewKey();
        await using var held = await sut.LockAsync(key);

        Assert.Null(await sut.TryLockAsync(key, TimeSpan.Zero));
    }

    [SkippableFact]
    public async Task Try_lock_times_out_on_a_held_key_and_returns_null()
    {
        var sut = CreateLock();
        var key = NewKey();
        await using var held = await sut.LockAsync(key);

        Assert.Null(await sut.TryLockAsync(key, TimeSpan.FromMilliseconds(50)));
    }

    // 释放后立即可再取：否则一次失败的临界区会永久锁死这个键。
    [SkippableFact]
    public async Task Releasing_the_handle_frees_the_key()
    {
        var sut = CreateLock();
        var key = NewKey();

        await (await sut.LockAsync(key)).DisposeAsync();

        await using var again = await sut.TryLockAsync(key, TimeSpan.Zero);
        Assert.NotNull(again);
    }

    // 不同键之间不得互相阻塞——按键互斥是这个接口存在的全部理由。
    [SkippableFact]
    public async Task Different_keys_do_not_block_each_other()
    {
        var sut = CreateLock();
        await using var first = await sut.LockAsync(NewKey());

        await using var second = await sut.TryLockAsync(NewKey(), TimeSpan.Zero);
        Assert.NotNull(second);
    }

    // 负超时是调用方的编程错误，必须当场抛而不是当成零或无限等待。
    [SkippableFact]
    public async Task Negative_timeout_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => CreateLock().TryLockAsync(NewKey(), TimeSpan.FromMilliseconds(-1)));
    }

    // 等待中被取消必须抛 OperationCanceledException，而不是返回 null——
    // 返回 null 会被调用方当成"锁被占用"而走降级路径，把取消吞掉。
    [SkippableFact]
    public async Task Cancellation_while_waiting_throws_rather_than_returning_null()
    {
        var sut = CreateLock();
        var key = NewKey();
        await using var held = await sut.LockAsync(key);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.TryLockAsync(key, TimeSpan.FromSeconds(10), cts.Token));
    }

    [SkippableFact]
    public async Task Cancellation_while_blocking_throws()
    {
        var sut = CreateLock();
        var key = NewKey();
        await using var held = await sut.LockAsync(key);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.LockAsync(key, cts.Token));
    }

    // 取消或超时之后这个键必须仍然可用：失败路径若没有归还内部租约，
    // 键会被永久占住，而症状要到很久以后才显形。
    [SkippableFact]
    public async Task A_failed_attempt_does_not_leak_the_key()
    {
        var sut = CreateLock();
        var key = NewKey();

        var held = await sut.LockAsync(key);
        Assert.Null(await sut.TryLockAsync(key, TimeSpan.Zero));
        await held.DisposeAsync();

        await using var again = await sut.TryLockAsync(key, TimeSpan.Zero);
        Assert.NotNull(again);
    }

    // 未失效时 LockLost 不得被取消，否则调用方关联它之后会立刻被打断。
    [SkippableFact]
    public async Task Lock_lost_is_not_signalled_while_the_handle_is_held()
    {
        await using var handle = await CreateLock().LockAsync(NewKey());

        Assert.False(handle.LockLost.IsCancellationRequested);
    }

    // 并发争用下同一时刻只能有一个持有者。
    [SkippableFact]
    public async Task Only_one_caller_holds_the_key_at_a_time()
    {
        var sut = CreateLock();
        var key = NewKey();
        var concurrent = 0;
        var maxObserved = 0;

        await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
        {
            await using var handle = await sut.LockAsync(key);
            var current = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref maxObserved, current);
            await Task.Delay(5);
            Interlocked.Decrement(ref concurrent);
        }));

        Assert.Equal(1, maxObserved);

        static void InterlockedMax(ref int target, int value)
        {
            int seen;
            do
            {
                seen = Volatile.Read(ref target);
                if (value <= seen) return;
            }
            while (Interlocked.CompareExchange(ref target, value, seen) != seen);
        }
    }
}
