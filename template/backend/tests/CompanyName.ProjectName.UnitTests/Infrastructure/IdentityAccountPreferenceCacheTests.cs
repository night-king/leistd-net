#if (RemoteTokenAuth)
using System.Net;
using System.Net.Http.Json;
using CompanyName.ProjectName.Domain.Users.Abstractions;
using CompanyName.ProjectName.Infrastructure.AccountPreferences;
using Leistd.ServiceClient.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CompanyName.ProjectName.UnitTests.Infrastructure;

/// <summary>资源服务向 Identity 读取账户偏好的缓存：新鲜期、条件请求、旧值回落、认证失效与代次。</summary>
/// <remarks>假接口按调用顺序给出响应；用 <see cref="TaskCompletionSource{TResult}"/> 控制响应到达的先后，复现交错。</remarks>
public sealed class IdentityAccountPreferenceCacheTests
{
    private static readonly AccountPreferenceCacheKey Key = new(null, Guid.CreateVersion7());

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero));
    private readonly ScriptedReadApi _api = new();

    [Fact]
    public async Task Server_reads_within_the_fresh_window_do_not_go_back_to_Identity()
    {
        var cache = CreateCache();
        _api.Respond(_ => Ok("dark", "\"v1\""));

        Assert.Equal("dark", Theme(await cache.ReadServerAsync(Key, default)));
        Assert.Equal("dark", Theme(await cache.ReadServerAsync(Key, default)));
        Assert.Equal(1, _api.Calls);
    }

    // 当前用户的 /me 每次都条件重验证：别处刚改的值，切回页签就要看到
    [Fact]
    public async Task Foreground_reads_revalidate_with_the_etag_even_while_fresh()
    {
        var cache = CreateCache();
        _api.Respond(_ => Ok("dark", "\"v1\""));
        _api.Respond(etag => etag == "\"v1\"" ? Status(HttpStatusCode.NotModified) : Ok("light", "\"v2\""));
        await cache.ReadServerAsync(Key, default);

        Assert.Equal("dark", Theme(await cache.ReadForegroundAsync(Key, default)));
        Assert.Equal(2, _api.Calls);
        Assert.Equal("\"v1\"", _api.LastIfNoneMatch);
    }

    [Fact]
    public async Task Server_reads_revalidate_once_the_fresh_window_has_passed()
    {
        var cache = CreateCache();
        _api.Respond(_ => Ok("dark", "\"v1\""));
        _api.Respond(_ => Ok("light", "\"v2\""));
        await cache.ReadServerAsync(Key, default);

        _time.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal("light", Theme(await cache.ReadServerAsync(Key, default)));
        Assert.Equal("\"v1\"", _api.LastIfNoneMatch);
    }

    [Fact]
    public async Task An_upstream_failure_falls_back_to_the_earlier_representation()
    {
        var cache = CreateCache();
        _api.Respond(_ => Ok("dark", "\"v1\""));
        _api.Respond(_ => Status(HttpStatusCode.ServiceUnavailable));
        await cache.ReadServerAsync(Key, default);

        Assert.Equal("dark", Theme(await cache.ReadForegroundAsync(Key, default)));
    }

    // 没有旧表示时不拿本地默认值冒充：暂时读不到
    [Fact]
    public async Task An_upstream_failure_without_an_earlier_representation_is_unknown()
    {
        var cache = CreateCache();
        _api.Respond(_ => Status(HttpStatusCode.ServiceUnavailable));

        Assert.Null(await cache.ReadForegroundAsync(Key, default));
    }

    [Fact]
    public async Task An_earlier_representation_is_not_used_after_it_expires()
    {
        var cache = CreateCache();
        _api.Respond(_ => Ok("dark", "\"v1\""));
        _api.Respond(_ => Status(HttpStatusCode.ServiceUnavailable));
        await cache.ReadServerAsync(Key, default);

        _time.Advance(TimeSpan.FromHours(25));

        Assert.Null(await cache.ReadServerAsync(Key, default));
        Assert.Null(_api.LastIfNoneMatch);
    }

    // 机器身份被拒：旧值作废，之后的 5xx 也不能把它恢复出来
    [Fact]
    public async Task A_machine_rejection_clears_the_earlier_representation_for_good()
    {
        var cache = CreateCache();
        _api.Respond(_ => Ok("dark", "\"v1\""));
        _api.Respond(_ => Status(HttpStatusCode.Unauthorized));
        _api.Respond(_ => Status(HttpStatusCode.ServiceUnavailable));
        await cache.ReadServerAsync(Key, default);

        var rejection = await Assert.ThrowsAsync<RemoteServiceException>(() => cache.ReadForegroundAsync(Key, default));
        Assert.Equal(401, rejection.RemoteStatusCode);

        Assert.Null(await cache.ReadForegroundAsync(Key, default));
    }

    // 旧 200 在途 → 另一次读取遇 401/403 → 旧 200 返回：不得发布，之后 5xx 也没有旧值可用
    [Fact]
    public async Task A_read_started_before_a_machine_rejection_cannot_republish_its_result()
    {
        var cache = CreateCache();
        var late = new TaskCompletionSource<HttpResponseMessage>();
        _api.Respond(_ => late.Task);
        _api.Respond(_ => Status(HttpStatusCode.Forbidden));
        _api.Respond(_ => Status(HttpStatusCode.ServiceUnavailable));
        _api.Respond(_ => Status(HttpStatusCode.ServiceUnavailable));

        var earlier = cache.ReadForegroundAsync(Key, default);
        await Assert.ThrowsAsync<RemoteServiceException>(() => cache.ReadForegroundAsync(Key, default));
        late.SetResult(await Ok("dark", "\"v1\""));

        // 旧结果作废后按新状态重读，遇到 5xx 时没有旧值
        Assert.Null(await earlier);
        Assert.Null(await cache.ReadForegroundAsync(Key, default));
    }

    // 旧读取在途 → 本人写入成功 → 旧读取返回：不覆盖写入的新值
    [Fact]
    public async Task A_read_started_before_a_write_cannot_overwrite_the_written_value()
    {
        var cache = CreateCache();
        var late = new TaskCompletionSource<HttpResponseMessage>();
        _api.Respond(_ => late.Task);
        _api.Respond(etag => etag == "\"v2\"" ? Status(HttpStatusCode.NotModified) : Ok("unexpected", "\"v9\""));

        var earlier = cache.ReadForegroundAsync(Key, default);
        cache.StoreAfterWrite(Key, Representation("light"), "\"v2\"");
        late.SetResult(await Ok("dark", "\"v1\""));

        Assert.Equal("light", Theme(await earlier));
        Assert.Equal("light", Theme(await cache.ReadServerAsync(Key, default)));
    }

    [Fact]
    public async Task Concurrent_server_reads_share_one_request()
    {
        var cache = CreateCache();
        var response = new TaskCompletionSource<HttpResponseMessage>();
        _api.Respond(_ => response.Task);

        var first = cache.ReadServerAsync(Key, default);
        var second = cache.ReadServerAsync(Key, default);
        response.SetResult(await Ok("dark", "\"v1\""));

        Assert.Equal("dark", Theme(await first));
        Assert.Equal("dark", Theme(await second));
        Assert.Equal(1, _api.Calls);
    }

    // 一个等待者取消，不能牵连共用同一次回源的其他等待者
    [Fact]
    public async Task One_cancelled_waiter_does_not_cancel_the_shared_request()
    {
        var cache = CreateCache();
        var response = new TaskCompletionSource<HttpResponseMessage>();
        _api.Respond(_ => response.Task);
        using var cancelled = new CancellationTokenSource();

        var abandoned = cache.ReadServerAsync(Key, cancelled.Token);
        var kept = cache.ReadServerAsync(Key, default);
        await cancelled.CancelAsync();
        response.SetResult(await Ok("dark", "\"v1\""));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        Assert.Equal("dark", Theme(await kept));
    }

    // 前台读取不加入在途的服务端回源：那次回源可能早于别处的写入
    [Fact]
    public async Task A_foreground_read_does_not_join_a_server_read_in_flight()
    {
        var cache = CreateCache();
        var serverResponse = new TaskCompletionSource<HttpResponseMessage>();
        _api.Respond(_ => serverResponse.Task);
        _api.Respond(_ => Ok("light", "\"v2\""));

        var server = cache.ReadServerAsync(Key, default);
        Assert.Equal("light", Theme(await cache.ReadForegroundAsync(Key, default)));
        serverResponse.SetResult(await Ok("dark", "\"v1\""));
        await server;

        Assert.Equal(2, _api.Calls);
        // 先发出、后返回的服务端回源不覆盖后发出的前台读取已发布的值
        Assert.Equal("light", Theme(await cache.ReadServerAsync(Key, default)));
        Assert.Equal(2, _api.Calls);
    }

    // 单例缓存不能随接触过的用户无限增长：闲置超过保留期的条目连同代次一起释放
    [Fact]
    public async Task Idle_entries_are_released_after_the_retention_window()
    {
        var cache = CreateCache();
        for (var i = 0; i < 100; i++)
        {
            _api.Respond(_ => Ok("dark", "\"v1\""));
            await cache.ReadServerAsync(new AccountPreferenceCacheKey(null, Guid.CreateVersion7()), default);
        }

        Assert.Equal(100, cache.Count);

        _time.Advance(TimeSpan.FromHours(25));
        _api.Respond(_ => Ok("dark", "\"v1\""));
        await cache.ReadServerAsync(new AccountPreferenceCacheKey(null, Guid.CreateVersion7()), default);

        Assert.Equal(1, cache.Count);
    }

    // 清扫跳过还被在途读取持有的条目：读取返回后照常发布到同一个条目，不会出现第二个协调对象
    [Fact]
    public async Task A_sweep_keeps_an_entry_whose_read_is_still_in_flight()
    {
        var cache = CreateCache();
        var pending = new TaskCompletionSource<HttpResponseMessage>();
        _api.Respond(_ => pending.Task);
        var read = cache.ReadForegroundAsync(Key, default);

        _time.Advance(TimeSpan.FromHours(25));
        _api.Respond(_ => Ok("light", "\"other\""));
        await cache.ReadServerAsync(new AccountPreferenceCacheKey(null, Guid.CreateVersion7()), default);
        pending.SetResult(await Ok("dark", "\"v1\""));

        Assert.Equal("dark", Theme(await read));
        var calls = _api.Calls;
        Assert.Equal("dark", Theme(await cache.ReadServerAsync(Key, default)));
        Assert.Equal(calls, _api.Calls);
    }

    private IdentityAccountPreferenceCache CreateCache()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IIdentityAccountPreferencesReadApi>(_api);
        services.AddOptions<AccountPreferencesClientOptions>();
        var provider = services.BuildServiceProvider();
        return new IdentityAccountPreferenceCache(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptionsMonitor<AccountPreferencesClientOptions>>(),
            _time,
            NullLogger<IdentityAccountPreferenceCache>.Instance);
    }

    private static AccountPreferenceSnapshot Representation(string theme) => new()
    {
        Preferences = new AccountPreferenceValues { Theme = theme },
        PreferenceOverrides = new AccountPreferenceValues { Theme = theme },
    };

    private static string? Theme(AccountPreferenceSnapshot? representation) => representation?.Preferences.Theme;

    private static Task<HttpResponseMessage> Ok(string theme, string etag)
    {
        // 线上的形状：与 Identity 的 /me/preferences 响应相同
        var body = new { preferences = new { theme }, preferenceOverrides = new { theme } };
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag);
        return Task.FromResult(response);
    }

    private static Task<HttpResponseMessage> Status(HttpStatusCode status)
        => Task.FromResult(new HttpResponseMessage(status));

    private sealed class ScriptedReadApi : IIdentityAccountPreferencesReadApi
    {
        private readonly Queue<Func<string?, Task<HttpResponseMessage>>> _responses = new();
        private int _calls;

        public int Calls => _calls;

        public string? LastIfNoneMatch { get; private set; }

        public void Respond(Func<string?, Task<HttpResponseMessage>> response) => _responses.Enqueue(response);

        public Task<HttpResponseMessage> GetAsync(string tenant, Guid userId, string? ifNoneMatch, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            LastIfNoneMatch = ifNoneMatch;
            return _responses.Dequeue()(ifNoneMatch);
        }
    }
}
#endif
