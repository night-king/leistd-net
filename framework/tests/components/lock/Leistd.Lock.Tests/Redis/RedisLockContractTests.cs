using Leistd.Lock.Redis;
using Leistd.Lock.Redis.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Leistd.Lock.Memory;
using Xunit;
using Leistd.Lock.Abstractions;

namespace Leistd.Lock.Tests.Redis;

/// <summary>Redis 锁的契约边界：零超时语义与选项启动校验。</summary>
public class RedisLockContractTests
{
    /// <summary>零超时必须"尝试一次"，与内存实现一致。</summary>
    /// <remarks>
    /// 若 <c>TryLockAsync</c> 写成 <c>while (now &lt; deadline)</c>，
    /// <c>TimeSpan.Zero</c> 使循环一次都不进——同一个 <see cref="ILock"/> 契约在两个实现上
    /// 给出不同行为，而调用方按接口编程时看不出来。
    /// 本仓库的内存锁测试（MemoryLocalLockCleanupTests 等）多条断言正建立在"零超时会尝试一次"之上。
    /// </remarks>
    private static Task<ILockHandle?> RunAsync(
        Func<ILockHandle?> attempt, TimeSpan timeout, TimeSpan? retryInterval = null)
        // 调用生产实现本身（internal，经 InternalsVisibleTo 可见），不复制循环逻辑
        => RedisDistributedLock.TryAcquireWithRetryAsync(
            _ => Task.FromResult(attempt()),
            timeout,
            retryInterval ?? TimeSpan.FromMilliseconds(5),
            TimeProvider.System,
            onTimeout: () => { },
            CancellationToken.None);

    [Fact]
    public async Task Zero_timeout_attempts_once_instead_of_skipping()
    {
        var attempts = 0;

        var handle = await RunAsync(() => { attempts++; return null; }, TimeSpan.Zero);

        Assert.Null(handle);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Zero_timeout_returns_the_handle_when_the_lock_is_free()
    {
        var handle = await RunAsync(() => new NoopHandle(), TimeSpan.Zero);

        Assert.NotNull(handle);
    }

    [Fact]
    public async Task A_positive_timeout_retries_until_it_elapses()
    {
        var attempts = 0;

        var handle = await RunAsync(
            () => { attempts++; return null; },
            timeout: TimeSpan.FromMilliseconds(60),
            retryInterval: TimeSpan.FromMilliseconds(5));

        Assert.Null(handle);
        Assert.True(attempts > 1, $"expected more than one attempt, got {attempts}");
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_retry_loop()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            RedisDistributedLock.TryAcquireWithRetryAsync(
                _ => Task.FromResult<ILockHandle?>(null),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(5),
                TimeProvider.System,
                onTimeout: () => { },
                cts.Token));
    }

    [Theory]
    [InlineData(-1)]        // Timeout.InfiniteTimeSpan 的毫秒值：交给 SemaphoreSlim 会变成"无限等待"
    [InlineData(-5000)]
    public async Task A_negative_timeout_is_rejected_by_both_implementations(int milliseconds)
    {
        // 统一契约：负值是编程错误。若内存实现把它交给 SemaphoreSlim
        //（-1ms 无限等待、更小的负数抛异常）、Redis 实现一律"试一次后返回 null"，
        // 同一个 ILock 接口就有三种行为，调用方无从依赖
        var timeout = TimeSpan.FromMilliseconds(milliseconds);

        using var memory = new MemoryLocalLock(NullLogger<MemoryLocalLock>.Instance);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => memory.TryLockAsync("k", timeout));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => CreateRedisLock().TryLockAsync("k", timeout));
    }

    private static ILock CreateRedisLock()
        // 参数守卫在触碰 Redis 之前抛出，multiplexer 只被构造函数捕获、从不解引用，
        // 因此这里传 null 是安全的。为此实现 IConnectionMultiplexer（两百余个成员）
        // 不成比例，而造一个"所有成员都抛异常"的替身除了噪声也不提供额外保障
        => new RedisDistributedLock(
            connectionMultiplexer: null!,
            Options.Create(new RedisLockOptions()),
            NullLogger<RedisDistributedLock>.Instance);


    private static IServiceProvider BuildHost(Action<RedisLockOptions> configure)
        => new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddRedisDistributedLock("localhost:6379", configure)
            .BuildServiceProvider();

    private static void ForceValidation(IServiceProvider provider)
        // ValidateOnStart 由宿主启动触发；单测里直接取值即可跑到同一条校验链
        => _ = provider.GetRequiredService<IOptions<RedisLockOptions>>().Value;

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_expiry_fails_validation(int seconds)
    {
        // 非正租约让锁立即失效或被 provider 拒绝——互斥当场不成立，且配置阶段毫无信号
        var provider = BuildHost(o => o.Expiry = TimeSpan.FromSeconds(seconds));

        var exception = Assert.Throws<OptionsValidationException>(() => ForceValidation(provider));
        Assert.Contains("Expiry", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_retry_interval_fails_validation(int milliseconds)
    {
        // 零重试间隔 = 对 Redis 忙轮询：Redis CPU 打满而应用日志一切正常
        var provider = BuildHost(o => o.RetryInterval = TimeSpan.FromMilliseconds(milliseconds));

        var exception = Assert.Throws<OptionsValidationException>(() => ForceValidation(provider));
        Assert.Contains("RetryInterval", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_options_pass_validation()
    {
        var provider = BuildHost(_ => { });

        ForceValidation(provider);
    }

    [Fact]
    public void A_null_key_prefix_is_normalized_rather_than_rejected()
    {
        // 配置里显式写 "KeyPrefix": null 时绑定器会置空；下游拼 key 会 NRE。
        // 这是无害情形，归一化而不是阻断启动
        var options = new RedisLockOptions { KeyPrefix = null! };

        Assert.Equal(string.Empty, options.KeyPrefix);
    }


    private sealed class NoopHandle : ILockHandle
    {
        public CancellationToken LockLost => CancellationToken.None;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}
