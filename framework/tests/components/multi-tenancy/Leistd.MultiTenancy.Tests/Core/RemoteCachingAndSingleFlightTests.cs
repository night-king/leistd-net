using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Exceptions;
using Leistd.ServiceClient.Exceptions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Leistd.MultiTenancy.Tests.TestDoubles;

namespace Leistd.MultiTenancy.Tests.Core;

/// <summary>缓存与单飞：同租户同连接名的并发只回源一次，调用方取消只取消自己的等待。</summary>
public sealed class RemoteCachingAndSingleFlightTests : IDisposable
{
    private readonly RemoteHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_resolved_route_is_served_from_cache()
    {
        _host.Tenant.Id = Guid.NewGuid();

        await _host.ResolveAsync();
        await _host.ResolveAsync();

        Assert.Equal(1, _host.Source.RuntimeCalls);
    }

    [Fact]
    public async Task Each_tenant_has_its_own_cache_entry()
    {
        _host.Tenant.Id = Guid.NewGuid();
        await _host.ResolveAsync();
        _host.Tenant.Id = Guid.NewGuid();
        await _host.ResolveAsync();

        Assert.Equal(2, _host.Source.RuntimeCalls);
    }

    // 缓存键带连接名：同一个租户在 crm 与 foundation 是两条路由，不能互相顶掉
    [Fact]
    public async Task Each_connection_name_has_its_own_cache_entry()
    {
        _host.Tenant.Id = Guid.NewGuid();
        _host.Source.Lookup = (id, name) => ScriptedRemoteSource.Hit(id, name, $"Data Source={name}");

        Assert.Equal("Data Source=crm", await _host.ResolveAsync("Crm"));
        Assert.Equal("Data Source=foundation", await _host.ResolveAsync("Foundation"));

        Assert.Equal(2, _host.Source.RuntimeCalls);
    }

    // 大小写只是写法差异，不能占两份缓存——否则一份命中一份回源，行为随调用点写法漂移
    [Fact]
    public async Task Names_differing_only_in_case_share_one_cache_entry()
    {
        _host.Tenant.Id = Guid.NewGuid();
        _host.Source.Lookup = (id, name) => ScriptedRemoteSource.Hit(id, name, "Data Source=acme-crm");

        await _host.ResolveAsync("Crm");
        await _host.ResolveAsync("crm");
        await _host.ResolveAsync("CRM");

        Assert.Equal(1, _host.Source.RuntimeCalls);
    }

    [Fact]
    public async Task Concurrent_requests_for_one_tenant_share_a_single_remote_call()
    {
        _host.Tenant.Id = Guid.NewGuid();
        _host.Source.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var callers = Enumerable.Range(0, 8).Select(_ => _host.ResolveAsync()).ToArray();
        await WaitUntilAsync(() => _host.Source.RuntimeCalls > 0);
        _host.Source.Gate.SetResult();
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, _host.Source.RuntimeCalls);
        Assert.All(results, result => Assert.Equal(RemoteHost.DefaultConnection, result));
    }

    // 发起者取消不能连带取消搭车者，也不能让下一个调用方重新回源
    [Fact]
    public async Task A_cancelled_caller_does_not_cancel_the_shared_resolution()
    {
        _host.Tenant.Id = Guid.NewGuid();
        _host.Source.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var cancelled = _host.ResolveAsync(cancellationToken: cts.Token);
        await WaitUntilAsync(() => _host.Source.RuntimeCalls > 0);
        var rider = _host.ResolveAsync();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        _host.Source.Gate.SetResult();

        Assert.Equal(RemoteHost.DefaultConnection, await rider);
        Assert.Equal(1, _host.Source.RuntimeCalls);
    }

    // 所有等待者都取消时回源随之取消（HybridCache 的语义），不留半截结果：下一次调用重新回源
    [Fact]
    public async Task When_every_caller_cancels_the_next_call_resolves_again()
    {
        _host.Tenant.Id = Guid.NewGuid();
        _host.Source.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var cancelled = _host.ResolveAsync(cancellationToken: cts.Token);
        await WaitUntilAsync(() => _host.Source.RuntimeCalls > 0);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        _host.Source.Gate = null;
        Assert.Equal(RemoteHost.DefaultConnection, await _host.ResolveAsync());
        Assert.Equal(2, _host.Source.RuntimeCalls);
    }

    // 回源失败不缓存：控制面短暂不可用恢复后，下一次调用就能拿到路由，而不是等缓存过期
    [Fact]
    public async Task A_failed_resolution_is_not_cached()
    {
        _host.Tenant.Id = Guid.NewGuid();
        var fail = true;
        _host.Source.Lookup = (id, _) => fail
            ? throw new HttpRequestException("control plane unavailable")
            : new TenantConnectionLookupResult { TenantId = id, HasAnyConnection = false };

        await Assert.ThrowsAsync<HttpRequestException>(() => _host.ResolveAsync());
        fail = false;

        Assert.Equal(RemoteHost.DefaultConnection, await _host.ResolveAsync());
        Assert.Equal(2, _host.Source.RuntimeCalls);
    }

    // 宿主注册了分布式缓存（如 Redis）也不能写进去：值是含口令的连接串
    [Fact]
    public async Task Connection_strings_never_reach_a_distributed_cache()
    {
        var distributed = new RecordingDistributedCache();
        using var host = new RemoteHost(configure: services =>
            services.AddSingleton<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(distributed));
        var tenantId = Guid.NewGuid();
        host.Tenant.Id = tenantId;
        host.Source.Lookup = (id, name) => ScriptedRemoteSource.Hit(id, name, "Host=tenant;Password=secret");

        Assert.Equal("Host=tenant;Password=secret", await host.ResolveAsync());
        Assert.Equal("Host=tenant;Password=secret", await host.ResolveAsync());

        Assert.Equal(1, host.Source.RuntimeCalls);
        Assert.Equal(0, distributed.Writes);
        Assert.Equal(0, distributed.Reads);
    }

    /// <summary>只记连接路由的键：HybridCache 构造时会读自己的标签失效标记，那不是本组件的读写。</summary>
    private sealed class RecordingDistributedCache : Microsoft.Extensions.Caching.Distributed.IDistributedCache
    {
        public int Reads;
        public int Writes;

        private static bool IsRoute(string key) => key.Contains("tenant-connection", StringComparison.Ordinal);

        public byte[]? Get(string key) { if (IsRoute(key)) Interlocked.Increment(ref Reads); return null; }
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) { if (IsRoute(key)) Interlocked.Increment(ref Reads); return Task.FromResult<byte[]?>(null); }
        public void Set(string key, byte[] value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions options) { if (IsRoute(key)) Interlocked.Increment(ref Writes); }
        public Task SetAsync(string key, byte[] value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions options, CancellationToken token = default) { if (IsRoute(key)) Interlocked.Increment(ref Writes); return Task.CompletedTask; }
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) { }
        public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Stale_routes_are_shared_throttled_and_discarded_after_a_rejection()
    {
        var clock = new FakeTimeProvider();
        using var host = new RemoteHost(cacheLifetime: "00:00:30", configure: services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.Configure<MemoryCacheOptions>(options => options.Clock = new CacheClock(clock));
        });
        host.Tenant.Id = Guid.NewGuid();
        Assert.Equal(RemoteHost.DefaultConnection, await host.ResolveAsync());
        clock.Advance(TimeSpan.FromSeconds(30));
        host.Source.Lookup = (_, _) => throw new TenantConnectionUnavailableException(
            new ServiceClientException("Unavailable", failureKind: ServiceClientFailureKind.Unavailable));
        Assert.Equal(RemoteHost.DefaultConnection, await host.ResolveAsync());
        Assert.Equal(RemoteHost.DefaultConnection, await host.ResolveAsync());
        Assert.Equal(2, host.Source.RuntimeCalls);
        clock.Advance(TimeSpan.FromSeconds(30));
        host.Source.Lookup = (_, _) => null;
        await Assert.ThrowsAsync<TenantNotFoundException>(() => host.ResolveAsync());
        host.Source.Lookup = (_, _) => throw new TenantConnectionUnavailableException(
            new ServiceClientException("Unavailable", failureKind: ServiceClientFailureKind.Unavailable));
        await Assert.ThrowsAsync<ServiceClientException>(() => host.ResolveAsync());
    }

    [Fact]
    public async Task A_pending_route_returns_stale_within_one_second_and_throttles_all_waiters()
    {
        var clock = new BudgetClock();
        var services = new ServiceCollection().AddLogging();
        services.Configure<MemoryCacheOptions>(options => options.Clock = new CacheClock(clock));
        services.AddHybridCache();
        await using var provider = services.BuildServiceProvider();
        var logger = new FakeLogger();
        var cache = new TenantRouteCache(provider.GetRequiredService<HybridCache>(), clock, logger);
        var calls = 0;
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask<string> Fetch(CancellationToken ct) => ++calls == 1
            ? ValueTask.FromResult("old-route") : new(release.Task.WaitAsync(ct));
        ValueTask<string> Read() => cache.GetAsync("route", TimeSpan.FromSeconds(30), TimeSpan.FromHours(1),
            Fetch, static _ => true, default);
        Assert.Equal("old-route", await Read());
        clock.Advance(TimeSpan.FromSeconds(30));
        var readers = Enumerable.Range(0, 3).Select(_ => Read().AsTask()).ToArray();
        try
        {
            // 旧实现没有预算计时器，保护性超时使回归在修复前明确失败。
            await clock.BudgetStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            clock.Advance(TimeSpan.FromMilliseconds(999));
            Assert.All(readers, reader => Assert.False(reader.IsCompleted));
            clock.Advance(TimeSpan.FromMilliseconds(1));
            Assert.All(await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(2)), value => Assert.Equal("old-route", value));
            Assert.Equal("old-route", await Read());
            Assert.Equal(2, calls);
            Assert.Single(logger.Collector.GetSnapshot(), record => record.Level == LogLevel.Warning);
            var completion = provider.GetRequiredService<HybridCache>().GetOrCreateAsync<string>("route:fetch",
                static _ => throw new InvalidOperationException("The original fetch must still be running."),
                new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableDistributedCache | HybridCacheEntryFlags.DisableLocalCacheWrite }).AsTask();
            clock.Advance(TimeSpan.FromSeconds(10));
            release.SetException(new HttpRequestException("unavailable"));
            Assert.Equal("old-route", await completion);
            Assert.Single(logger.Collector.GetSnapshot(), record => record.Level == LogLevel.Warning);
            Assert.Equal("old-route", await Read());
        }
        finally
        {
            release.TrySetResult("new-route");
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("rejection")]
    [InlineData("deadline")]
    [InlineData("cancellation")]
    [InlineData("cold")]
    public async Task Bounded_route_waiting_preserves_results_deadlines_and_cancellation(string scenario)
    {
        var clock = new BudgetClock();
        var services = new ServiceCollection().AddLogging();
        services.Configure<MemoryCacheOptions>(options => options.Clock = new CacheClock(clock));
        services.AddHybridCache();
        await using var provider = services.BuildServiceProvider();
        var hybrid = provider.GetRequiredService<HybridCache>();
        var cache = new TenantRouteCache(hybrid, clock, new FakeLogger());
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async ValueTask<string> Fetch(CancellationToken ct)
        {
            if (++calls == 1 && scenario != "cold") return "old";
            started.TrySetResult();
            return await release.Task.WaitAsync(ct);
        }
        using var cancellation = new CancellationTokenSource();
        Task<string> Read(CancellationToken ct = default) => cache.GetAsync("boundary", TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(1), Fetch, static ex => ex is HttpRequestException, ct).AsTask();
        if (scenario != "cold")
        {
            await Read();
            clock.Advance(TimeSpan.FromSeconds(30));
        }
        var reader = Read(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            if (scenario == "cold")
            {
                clock.Advance(TimeSpan.FromSeconds(2));
                Assert.False(reader.IsCompleted);
                release.SetResult("new");
                Assert.Equal("new", await reader);
                return;
            }
            await clock.BudgetStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (scenario == "cancellation")
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader);
                return;
            }
            if (scenario == "rejection")
            {
                release.SetException(new InvalidOperationException("rejected"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => reader);
                await Assert.ThrowsAsync<InvalidOperationException>(() => Read());
                return;
            }
            clock.Advance(TimeSpan.FromSeconds(1));
            if (scenario == "deadline")
            {
                // 等待预算到达时旧值也刚好到达硬截止，必须继续等待权威结果。
                release.SetException(new HttpRequestException("unavailable"));
                await Assert.ThrowsAsync<HttpRequestException>(() => reader);
                return;
            }
            // 成功在预算内返回，不能错误进入节流。
            release.SetResult("new");
            Assert.Equal("new", await reader);
            Assert.Equal("new", await Read());
            Assert.Equal(2, calls);
        }
        finally
        {
            release.TrySetResult("new");
        }
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }
}
