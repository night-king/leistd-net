using Leistd.ServiceClient.OAuth.Handlers;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
