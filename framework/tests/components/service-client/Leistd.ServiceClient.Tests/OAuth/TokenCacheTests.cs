using Leistd.ServiceClient.OAuth.Handlers;
using Leistd.ServiceClient.Exceptions;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Leistd.ServiceClient.Tests.OAuth;

public sealed class TokenCacheTests
{
    // 升级 HybridCache 时先移除生产复查，确认本用例仍拒绝迟到 miss，再恢复；会话移除须早于完成信号。
    [Fact]
    public async Task A_delayed_cache_miss_reuses_the_completed_fetch()
    {
        using var memory = new PausingMemory(new MemoryCache(new MemoryCacheOptions()));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMemoryCache>(memory);
        services.AddHybridCache();
        await using var provider = services.BuildServiceProvider();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero));
        var cache = new TokenCache(provider.GetRequiredService<HybridCache>(), provider.GetRequiredService<ILogger<TokenCache>>(), clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        async ValueTask<TokenCache.Token> Fetch(CancellationToken token)
        {
            var serial = Interlocked.Increment(ref count);
            started.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            return new TokenCache.Token($"token-{serial}", clock.GetUtcNow().AddMinutes(10));
        }
        var key = TokenCache.Key("race");
        memory.Key = key;
        var first = cache.GetAsync(key, TimeSpan.FromSeconds(10), Fetch, default).AsTask();
        Task<TokenCache.Token>? second = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            memory.Arm();
            // IMemoryCache 的读接口是同步的，使用专用线程暂停旧 miss，不阻塞线程池。
            second = Task.Factory.StartNew(() => cache.GetAsync(key, TimeSpan.FromSeconds(10), Fetch, default).AsTask(),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            await memory.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            release.TrySetResult();
            Assert.Equal("token-1", (await first.WaitAsync(TimeSpan.FromSeconds(10))).Value);
            memory.Resume.Set();
            Assert.Equal("token-1", (await second.WaitAsync(TimeSpan.FromSeconds(10))).Value);
            Assert.Equal(1, count);
        }
        finally
        {
            release.TrySetResult();
            memory.Resume.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Machine_renewal_reuses_only_a_current_usable_token(bool invalidate)
    {
        var clock = new FakeTimeProvider();
        var services = new ServiceCollection().AddLogging();
        services.Configure<MemoryCacheOptions>(options => options.Clock = new CacheClock(clock));
        services.AddHybridCache();
        await using var provider = services.BuildServiceProvider();
        var cache = new TokenCache(provider.GetRequiredService<HybridCache>(), provider.GetRequiredService<ILogger<TokenCache>>(), clock);
        var calls = 0;
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask<TokenCache.Token> Fetch(CancellationToken ct)
        {
            if (++calls == 1) return new("old-token", clock.GetUtcNow().AddHours(2));
            began.TrySetResult();
            await release.Task.WaitAsync(ct);
            throw new ServiceClientException("Unavailable", failureKind: ServiceClientFailureKind.Unavailable);
        }
        var key = TokenCache.Key("renewal");
        Assert.Equal("old-token", (await cache.GetAsync(key, TimeSpan.FromSeconds(60), Fetch, default, true)).Value);
        clock.Advance(TimeSpan.FromMinutes(61));
        var renewal = cache.GetAsync(key, TimeSpan.FromSeconds(60), Fetch, default, true).AsTask();
        await began.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var waiters = invalidate ? [] : Enumerable.Range(0, 8)
            .Select(_ => cache.GetAsync(key, TimeSpan.FromSeconds(60), Fetch, default, true).AsTask()).ToArray();
        if (invalidate) await cache.RemoveAsync(key, default);
        release.SetResult();
        if (invalidate)
            await Assert.ThrowsAsync<ServiceClientException>(() => renewal);
        else
        {
            Assert.Equal("old-token", (await renewal).Value);
            Assert.All(await Task.WhenAll(waiters), token => Assert.Equal("old-token", token.Value));
            Assert.Equal("old-token", (await cache.GetAsync(key, TimeSpan.FromSeconds(60), Fetch, default, true)).Value);
            Assert.Equal(2, calls);
            clock.Advance(TimeSpan.FromHours(1));
            await Assert.ThrowsAsync<ServiceClientException>(() => cache.GetAsync(key, TimeSpan.FromSeconds(60), Fetch, default, true).AsTask());
        }
    }

    [Fact]
    public async Task A_pending_machine_renewal_returns_stale_within_one_second()
    {
        var clock = new BudgetClock();
        var services = new ServiceCollection().AddLogging();
        services.Configure<MemoryCacheOptions>(options => options.Clock = new CacheClock(clock));
        services.AddHybridCache();
        await using var provider = services.BuildServiceProvider();
        var logger = new FakeLogger<TokenCache>();
        var cache = new TokenCache(provider.GetRequiredService<HybridCache>(), logger, clock);
        var calls = 0;
        var release = new TaskCompletionSource<TokenCache.Token>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask<TokenCache.Token> Fetch(CancellationToken ct) => ++calls == 1
            ? ValueTask.FromResult(new TokenCache.Token("old-token", clock.GetUtcNow().AddHours(2))) : new(release.Task.WaitAsync(ct));
        ValueTask<TokenCache.Token> Read() => cache.GetAsync("machine", TimeSpan.FromSeconds(60), Fetch, default, true);
        await Read();
        clock.Advance(TimeSpan.FromMinutes(61));
        var readers = Enumerable.Range(0, 3).Select(_ => Read().AsTask()).ToArray();
        try
        {
            await clock.BudgetStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.All(await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(2)), token => Assert.Equal("old-token", token.Value));
            Assert.Equal("old-token", (await Read()).Value);
            Assert.Equal(2, calls);
            Assert.Single(logger.Collector.GetSnapshot(), record => record.Level == LogLevel.Warning);
            var completion = provider.GetRequiredService<HybridCache>().GetOrCreateAsync<TokenCache.Token>("machine:fetch",
                static _ => throw new InvalidOperationException("The original renewal must still be running."),
                new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableDistributedCache | HybridCacheEntryFlags.DisableLocalCacheWrite }).AsTask();
            clock.Advance(TimeSpan.FromSeconds(30));
            var retry = Read().AsTask();
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal("old-token", (await retry).Value);
            Assert.Equal(2, calls);
            release.SetResult(new TokenCache.Token("new-token", clock.GetUtcNow().AddHours(2)));
            Assert.Equal("new-token", (await completion).Value);
            Assert.Equal("new-token", (await Read()).Value);
            Assert.Equal(2, calls);
        }
        finally
        {
            release.TrySetResult(new TokenCache.Token("new-token", clock.GetUtcNow().AddHours(2)));
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_timed_out_renewal_does_not_revive_an_invalidated_or_expired_token(bool expire)
    {
        var clock = new BudgetClock();
        var services = new ServiceCollection().AddLogging();
        services.Configure<MemoryCacheOptions>(options => options.Clock = new CacheClock(clock));
        services.AddHybridCache();
        await using var provider = services.BuildServiceProvider();
        var hybrid = provider.GetRequiredService<HybridCache>();
        var cache = new TokenCache(hybrid, new FakeLogger<TokenCache>(), clock);
        var release = new TaskCompletionSource<TokenCache.Token>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        ValueTask<TokenCache.Token> Fetch(CancellationToken ct) => ++calls == 1
            ? ValueTask.FromResult(new TokenCache.Token("old", clock.GetUtcNow().AddSeconds(122))) : new(release.Task.WaitAsync(ct));
        Task<TokenCache.Token> Read() => cache.GetAsync("invalidated", TimeSpan.FromSeconds(60), Fetch, default, true).AsTask();
        await Read();
        clock.Advance(TimeSpan.FromMilliseconds(expire ? 61500 : 61000));
        var reader = Read();
        await clock.BudgetStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (!expire) await cache.RemoveAsync("invalidated", default);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(reader.IsCompleted);
        release.SetException(new ServiceClientException("Unavailable", failureKind: ServiceClientFailureKind.Unavailable));
        await Assert.ThrowsAsync<ServiceClientException>(() => reader);
        await Assert.ThrowsAsync<ServiceClientException>(() => Read());
    }

    [Fact]
    public async Task A_background_machine_rejection_clears_the_timeout_fallback()
    {
        var clock = new BudgetClock();
        var services = new ServiceCollection().AddLogging();
        services.Configure<MemoryCacheOptions>(options => options.Clock = new CacheClock(clock));
        services.AddHybridCache();
        await using var provider = services.BuildServiceProvider();
        var hybrid = provider.GetRequiredService<HybridCache>();
        var cache = new TokenCache(hybrid, new FakeLogger<TokenCache>(), clock);
        var release = new TaskCompletionSource<TokenCache.Token>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        ValueTask<TokenCache.Token> Fetch(CancellationToken ct) => ++calls == 1
            ? ValueTask.FromResult(new TokenCache.Token("old", clock.GetUtcNow().AddHours(2))) : new(release.Task.WaitAsync(ct));
        Task<TokenCache.Token> Read() => cache.GetAsync("reject", TimeSpan.FromSeconds(60), Fetch, default, true).AsTask();
        await Read();
        clock.Advance(TimeSpan.FromMinutes(61));
        var reader = Read();
        await clock.BudgetStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var completion = hybrid.GetOrCreateAsync<TokenCache.Token>("reject:fetch",
            static _ => throw new InvalidOperationException("The original renewal must still be running."),
            new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableDistributedCache | HybridCacheEntryFlags.DisableLocalCacheWrite }).AsTask();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("old", (await reader).Value);
        release.SetException(new RemoteServiceException("Rejected", 400, "invalid_client") { IsOAuthError = true });
        await Assert.ThrowsAsync<RemoteServiceException>(() => completion);
        await Assert.ThrowsAsync<RemoteServiceException>(() => Read());
    }

    private sealed class BudgetClock : FakeTimeProvider
    {
        public TaskCompletionSource BudgetStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            BudgetStarted.TrySetResult();
            return timer;
        }
    }

    private sealed class CacheClock(TimeProvider clock) : ISystemClock
    {
        public DateTimeOffset UtcNow => clock.GetUtcNow();
    }

    private sealed class PausingMemory(IMemoryCache inner) : IMemoryCache
    {
        public string Key { get; set; } = "";
        private int armed;
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Resume { get; } = new();
        public void Arm() => Interlocked.Exchange(ref armed, 1);
        public bool TryGetValue(object key, out object? value)
        {
            var found = inner.TryGetValue(key, out value);
            if (!found && key.Equals(Key) && Interlocked.Exchange(ref armed, 0) == 1)
            {
                Paused.TrySetResult();
                if (!Resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The cache read was never released.");
            }
            return found;
        }
        public ICacheEntry CreateEntry(object key) => inner.CreateEntry(key);
        public void Remove(object key) => inner.Remove(key);
        public void Dispose() { inner.Dispose(); Resume.Dispose(); }
    }
}
