using System.Net;
using System.Text;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.ServiceClient;
using Leistd.MultiTenancy.ServiceClient.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Leistd.MultiTenancy.Tests.ServiceClient;

/// <summary>远端库目录的短时缓存：常驻服务按秒轮询逐库作业，期内只回源一次；失败不缓存，过期不返回陈旧清单。</summary>
/// <remarks>经 <c>AddRemoteTenantConnectionStore</c> 的真实注册与 HTTP 管道断言，只把最底层的网络换成脚本。</remarks>
public sealed class RemoteDatabaseDirectoryCachingTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private readonly FakeTimeProvider _clock = new();
    private readonly ScriptedControlPlane _controlPlane = new();
    private readonly ServiceProvider _provider;

    public RemoteDatabaseDirectoryCachingTests()
    {
        _provider = Build(lifetime: "00:00:30");
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public async Task A_listing_is_served_from_cache_until_the_lifetime_ends()
    {
        await ListAsync();
        _clock.Advance(TimeSpan.FromSeconds(29));
        await ListAsync();
        Assert.Equal(1, _controlPlane.Calls);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await ListAsync();
        Assert.Equal(2, _controlPlane.Calls);
    }

    [Fact]
    public async Task The_default_lifetime_is_thirty_seconds()
    {
        using var provider = Build(lifetime: null);

        await ListAsync(provider: provider);
        _clock.Advance(TimeSpan.FromSeconds(29));
        await ListAsync(provider: provider);
        Assert.Equal(1, _controlPlane.Calls);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await ListAsync(provider: provider);
        Assert.Equal(2, _controlPlane.Calls);
    }

    // 全集给清理与盘点用、活跃清单给发布用，互相顶掉会让一方拿到另一方的口径
    [Fact]
    public async Task Connection_name_and_active_only_each_have_their_own_entry()
    {
        await ListAsync("crm", activeOnly: true);
        await ListAsync("crm", activeOnly: false);
        await ListAsync("foundation", activeOnly: true);

        Assert.Equal(3, _controlPlane.Calls);
        Assert.Equal(
            ["name=crm&activeOnly=true", "name=crm&activeOnly=false", "name=foundation&activeOnly=true"],
            _controlPlane.Queries);
    }

    // 大小写只是写法差异：缓存键与发给控制面的名字是同一个归一化值
    [Fact]
    public async Task Names_differing_only_in_case_share_one_entry_and_reach_the_control_plane_lowercased()
    {
        await ListAsync("Crm");
        await ListAsync("CRM");

        Assert.Equal(1, _controlPlane.Calls);
        Assert.Equal(["name=crm&activeOnly=true"], _controlPlane.Queries);
    }

    // 命中直接复用实例：不复用就意味着每次都反序列化，且超过载荷上限的清单根本进不了缓存
    [Fact]
    public async Task A_hit_returns_the_cached_instance_even_for_a_large_listing()
    {
        _controlPlane.DatabaseCount = 12_000;

        var first = await ListAsync();
        var second = await ListAsync();

        Assert.True(Encoding.UTF8.GetByteCount(_controlPlane.LastBody!) > 1024 * 1024);
        Assert.Equal(12_000, first.Databases.Count);
        Assert.Same(first, second);
        Assert.Equal(1, _controlPlane.Calls);
    }

    // 部分租户解析失败是同一次回源结果的一部分，原样随清单返回，不能被缓存丢掉
    [Fact]
    public async Task Failed_tenants_are_kept_with_the_cached_listing()
    {
        await ListAsync();
        var cached = await ListAsync();

        var failure = Assert.Single(cached.FailedTenants);
        Assert.Equal((TenantId, "unresolved"), (failure.TenantId, failure.Reason));
        Assert.Equal(["fp-1"], cached.Databases.Select(x => x.Fingerprint));
    }

    [Fact]
    public async Task Concurrent_listings_share_a_single_remote_call()
    {
        _controlPlane.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var callers = Enumerable.Range(0, 8).Select(_ => ListAsync()).ToArray();
        await WaitUntilAsync(() => _controlPlane.Calls > 0);
        _controlPlane.Gate.SetResult();
        await Task.WhenAll(callers);

        Assert.Equal(1, _controlPlane.Calls);
    }

    // 发起者取消只取消自己的等待，搭车者照样拿到结果，结果也进缓存
    [Fact]
    public async Task A_cancelled_caller_does_not_cancel_the_shared_listing()
    {
        _controlPlane.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var cancelled = ListAsync(cancellationToken: cts.Token);
        await WaitUntilAsync(() => _controlPlane.Calls > 0);
        var rider = ListAsync();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        _controlPlane.Gate.SetResult();
        await rider;
        await ListAsync();

        Assert.Equal(1, _controlPlane.Calls);
    }

    [Fact]
    public async Task When_every_caller_cancels_the_next_listing_calls_again()
    {
        _controlPlane.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var cancelled = ListAsync(cancellationToken: cts.Token);
        await WaitUntilAsync(() => _controlPlane.Calls > 0);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        _controlPlane.Gate = null;
        await ListAsync();
        Assert.Equal(2, _controlPlane.Calls);
    }

    // 控制面短暂不可用恢复后，下一轮就能拿到清单，而不是等一个期限
    [Fact]
    public async Task A_failed_listing_is_not_cached()
    {
        _controlPlane.Status = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAnyAsync<Exception>(() => ListAsync());

        _controlPlane.Status = HttpStatusCode.OK;
        await ListAsync();

        Assert.Equal(2, _controlPlane.Calls);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    [InlineData("01:00:01")]
    public void An_out_of_range_lifetime_fails_with_the_configuration_key(string lifetime)
    {
        using var provider = Build(lifetime);

        var error = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<RemoteTenantConnectionClientOptions>>().Value);

        Assert.Contains("Leistd:ServiceClients:Identity:DatabaseDirectoryCacheLifetime", error.Message);
        Assert.Contains(TenantRouteCacheOptions.MaximumCacheLifetime.ToString(), error.Message);
    }

    [Fact]
    public void The_maximum_lifetime_is_accepted()
    {
        using var provider = Build("01:00:00");

        var options = provider.GetRequiredService<IOptions<RemoteTenantConnectionClientOptions>>().Value;

        Assert.Equal(TenantRouteCacheOptions.MaximumCacheLifetime, options.DatabaseDirectoryCacheLifetime);
    }

    private ServiceProvider Build(string? lifetime)
    {
        var settings = new Dictionary<string, string?> { ["Leistd:ServiceClients:Identity:BaseAddress"] = "https://identity.test" };
        if (lifetime is not null)
        {
            settings["Leistd:ServiceClients:Identity:DatabaseDirectoryCacheLifetime"] = lifetime;
        }

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddRemoteTenantConnectionStore("Identity").ConfigurePrimaryHttpMessageHandler(() => _controlPlane);
        // HybridCache 的一级缓存按 MemoryCache 的时钟判过期
        services.Configure<MemoryCacheOptions>(options => options.Clock = new FakeSystemClock(_clock));
        return services.BuildServiceProvider();
    }

    private async Task<TenantDatabaseListResult> ListAsync(
        string name = "crm",
        bool activeOnly = true,
        ServiceProvider? provider = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = (provider ?? _provider).CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITenantDatabaseDirectory>()
            .GetDatabasesAsync(name, activeOnly, cancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private sealed class FakeSystemClock(TimeProvider clock) : ISystemClock
    {
        public DateTimeOffset UtcNow => clock.GetUtcNow();
    }

    // 控制面 /databases 端点的脚本：可设状态码、清单规模与闸门
    private sealed class ScriptedControlPlane : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public List<string> Queries { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public int DatabaseCount { get; set; } = 1;
        public TaskCompletionSource? Gate { get; set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            lock (Queries)
            {
                Queries.Add(request.RequestUri!.Query.TrimStart('?'));
            }

            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(cancellationToken);
            }

            if (Status != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(Status) { RequestMessage = request };
            }

            var databases = string.Join(',', Enumerable.Range(1, DatabaseCount).Select(i =>
                $$"""{"fingerprint":"fp-{{i}}","tenantIds":["{{Guid.NewGuid()}}","{{Guid.NewGuid()}}"]}"""));
            LastBody = $$"""{"databases":[{{databases}}],"failedTenants":[{"tenantId":"{{TenantId}}","reason":"unresolved"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(LastBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
