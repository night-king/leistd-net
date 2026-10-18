using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Leistd.Lock.Abstractions;
using Leistd.Lock.Memory;
using Leistd.Security.AspNetCore;
using Leistd.Security.AspNetCore.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Leistd.Security.Tests;

public sealed class DistributedTicketStoreTests
{
    [Theory]
    [InlineData("callback")]
    [InlineData("subclass")]
    [InlineData("type")]
    public async Task Native_sign_in_keeps_tokens_server_side_and_old_references_cannot_revoke_a_new_login(string mode)
    {
        using var host = new Harness(mode);
        var first = await host.LoginAsync("A");
        var reference = host.Reference(first, "A");
        Assert.Empty(reference.Properties.GetTokens());
        var key = Assert.Single(reference.Principal.Claims).Value;
        var original = (await host.Store.RetrieveAsync(key))!;
        Assert.Equal("private-token", original.Properties.GetTokenValue("access_token"));
        Assert.Equal(host.Clock.GetUtcNow().AddMinutes(20), original.Properties.ExpiresUtc);

        var second = await host.LoginAsync("A", first, copy: true);
        Assert.Equal(key, Assert.Single(host.Reference(second, "A").Principal.Claims).Value);
        Assert.Equal(HttpStatusCode.Unauthorized, await host.MeAsync(first, "A"));
        Assert.Equal(HttpStatusCode.OK, await host.MeAsync(second, "A"));
        var oldContext = new DefaultHttpContext { RequestServices = host.Server.Services };
        oldContext.Request.Headers.Cookie = first;
        await host.Store.RemoveAsync(key, oldContext, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, await host.MeAsync(second, "A"));
        await host.Store.RenewAsync(key, original);
        Assert.Equal(HttpStatusCode.OK, await host.MeAsync(second, "A"));
        Assert.Equal(2, host.Events.SigningIn);
        Assert.Equal(2, host.Events.SignedIn);
    }

    [Fact]
    public async Task Native_sliding_renewal_keeps_the_reference_and_extends_server_expiration()
    {
        using var host = new Harness("callback");
        var cookie = await host.LoginAsync("A");
        var key = Assert.Single(host.Reference(cookie, "A").Principal.Claims).Value;
        Assert.Equal(HttpStatusCode.Unauthorized, await host.MeAsync(cookie, "B"));
        host.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.OK, await host.MeAsync(cookie, "A"));
        var renewed = (await host.Store.RetrieveAsync(key))!;
        Assert.Equal(host.Clock.GetUtcNow().AddMinutes(20), renewed.Properties.ExpiresUtc);
        Assert.Equal(HttpStatusCode.OK, await host.MeAsync(cookie, "A"));
        await host.Store.RemoveAsync(key);
        Assert.Equal(HttpStatusCode.Unauthorized, await host.MeAsync(cookie, "A"));
    }

    [Fact]
    public async Task Http_renewal_requires_the_matching_reference_and_tampered_cache_data_is_rejected()
    {
        using var host = new Harness("callback");
        var cookie = await host.LoginAsync("A");
        var key = Assert.Single(host.Reference(cookie, "A").Principal.Claims).Value;
        var ticket = (await host.Store.RetrieveAsync(key))!;
        ticket.Properties.Items["changed"] = "unauthenticated";
        var context = new DefaultHttpContext { RequestServices = host.Server.Services };
        await host.Store.RenewAsync(key, ticket, context, CancellationToken.None);
        Assert.False((await host.Store.RetrieveAsync(key))!.Properties.Items.ContainsKey("changed"));
        var bytes = (await host.Cache.GetAsync(key))!;
        bytes[^1] ^= 1;
        await host.Cache.SetAsync(key, bytes, new DistributedCacheEntryOptions());
        Assert.Null(await host.Store.RetrieveAsync(key));
    }

    [Fact]
    public async Task Named_schemes_keep_separate_native_transient_event_instances_within_one_request()
    {
        using var host = new Harness("type");
        using var response = await host.Client.GetAsync("/both-login");
        response.EnsureSuccessStatusCode();
        var instances = host.Events.InstanceIds;
        Assert.Equal(2, instances.Count);
        Assert.NotEqual(instances[0], instances[1]);
        await host.LoginAsync("A");
        Assert.Equal(3, instances.Count);
        Assert.NotEqual(instances[0], instances[2]);
    }

    /// <summary>预先配置的 callback、子类与 EventsType 的验证、挑战回调都经包装执行。</summary>
    [Theory]
    [InlineData("callback")]
    [InlineData("subclass")]
    [InlineData("type")]
    public async Task Pre_configured_validation_and_challenge_events_still_run(string mode)
    {
        using var host = new Harness(mode);
        var cookie = await host.LoginAsync("A");
        Assert.Equal(HttpStatusCode.OK, await host.MeAsync(cookie, "A"));
        Assert.Equal(1, host.Events.ValidatePrincipal);
        Assert.Equal(HttpStatusCode.Unauthorized, await host.ChallengeAsync("A"));
        Assert.Equal(1, host.Events.RedirectToLogin);
    }

    /// <summary>宿主在组件之后改回调按原生语义生效且只执行一次，组件仍给再次登录换版本。</summary>
    [Fact]
    public async Task Callbacks_assigned_after_the_component_run_once_and_sign_in_still_rotates_the_reference()
    {
        var signingIn = 0;
        var validated = 0;
        var redirected = 0;
        using var host = new Harness("callback", configure: builder =>
            builder.Services.PostConfigure<CookieAuthenticationOptions>("A", options =>
            {
                options.Events.OnSigningIn = _ => { signingIn++; return Task.CompletedTask; };
                options.Events.OnValidatePrincipal = _ => { validated++; return Task.CompletedTask; };
                options.Events.OnRedirectToLogin = context => { redirected++; context.Response.StatusCode = 401; return Task.CompletedTask; };
            }));
        var first = await host.LoginAsync("A");
        Assert.Equal(1, signingIn);
        Assert.Equal(HttpStatusCode.OK, await host.MeAsync(first, "A"));
        Assert.Equal(1, validated);
        Assert.Equal(HttpStatusCode.Unauthorized, await host.ChallengeAsync("A"));
        Assert.Equal(1, redirected);

        var second = await host.LoginAsync("A", first, copy: true);
        Assert.Equal(2, signingIn);
        Assert.Equal(HttpStatusCode.Unauthorized, await host.MeAsync(first, "A"));
        Assert.Equal(HttpStatusCode.OK, await host.MeAsync(second, "A"));
    }

    /// <summary>原事件子类重写的其余五个虚方法经包装各执行一次；方案 B 不受默认认证中间件干扰，计数只来自显式请求。</summary>
    [Fact]
    public async Task Pre_configured_subclass_overrides_for_the_remaining_events_each_run_once()
    {
        var probe = new RemainingEventsProbe();
        using var host = new Harness("callback", configure: builder =>
            builder.Services.Configure<CookieAuthenticationOptions>("B", options => options.Events = probe));
        var cookie = await host.LoginAsync("B");
        Assert.Empty(probe.Calls);

        Assert.Equal(HttpStatusCode.OK, await host.MeAsync(cookie, "B"));
        Assert.Equal(1, probe.Count(nameof(CookieAuthenticationEvents.CheckSlidingExpiration)));

        // 原生默认会 302，403 说明走的是重写
        using (var forbid = await host.Client.GetAsync("/forbid?scheme=B"))
            Assert.Equal(HttpStatusCode.Forbidden, forbid.StatusCode);
        Assert.Equal(1, probe.Count(nameof(CookieAuthenticationEvents.RedirectToAccessDenied)));

        Assert.Equal(HttpStatusCode.NoContent, await host.LogoutAsync(cookie, "B"));
        Assert.Equal(1, probe.Count(nameof(CookieAuthenticationEvents.SigningOut)));
        Assert.Equal(1, probe.Count(nameof(CookieAuthenticationEvents.RedirectToReturnUrl)));

        // 官方处理器不触发 RedirectToLogout，只能直接调包装后的事件对象
        var options = host.Options("B");
        Assert.IsNotType<RemainingEventsProbe>(options.Events);
        await options.Events.RedirectToLogout(await host.RedirectContextAsync("B"));
        Assert.Equal(1, probe.Count(nameof(CookieAuthenticationEvents.RedirectToLogout)));

        Assert.Equal(1, probe.Count(nameof(CookieAuthenticationEvents.CheckSlidingExpiration)));
        Assert.Equal(5, probe.Calls.Count);
    }

    /// <summary>组件之后赋值的回调取代对应转发：新回调执行一次，原子类重写不再执行。</summary>
    [Fact]
    public async Task Remaining_callbacks_assigned_after_the_component_replace_the_forwarding()
    {
        var probe = new RemainingEventsProbe();
        var sliding = 0;
        var denied = 0;
        var loggedOut = 0;
        using var host = new Harness("callback", configure: builder =>
        {
            builder.Services.Configure<CookieAuthenticationOptions>("B", options => options.Events = probe);
            builder.Services.PostConfigure<CookieAuthenticationOptions>("B", options =>
            {
                options.Events.OnCheckSlidingExpiration = _ => { sliding++; return Task.CompletedTask; };
                options.Events.OnRedirectToAccessDenied = context => { denied++; context.Response.StatusCode = 418; return Task.CompletedTask; };
                options.Events.OnRedirectToLogout = _ => { loggedOut++; return Task.CompletedTask; };
            });
        });
        var cookie = await host.LoginAsync("B");
        Assert.Equal(HttpStatusCode.OK, await host.MeAsync(cookie, "B"));
        Assert.Equal(1, sliding);
        using (var forbid = await host.Client.GetAsync("/forbid?scheme=B"))
            Assert.Equal((HttpStatusCode)418, forbid.StatusCode);
        Assert.Equal(1, denied);
        await host.Options("B").Events.RedirectToLogout(await host.RedirectContextAsync("B"));
        Assert.Equal(1, loggedOut);
        Assert.Empty(probe.Calls);

        // 未改写的回调仍转发
        Assert.Equal(HttpStatusCode.NoContent, await host.LogoutAsync(cookie, "B"));
        Assert.Equal(1, probe.Count(nameof(CookieAuthenticationEvents.SigningOut)));
    }

    /// <summary>组件包装之后整体替换事件或 Cookie 管理器会脱掉引用版本契约，启动即失败并点名方案。</summary>
    [Theory]
    [InlineData("events", "Events")]
    [InlineData("type", "EventsType")]
    [InlineData("manager", "CookieManager")]
    public void Replacing_wrapped_cookie_options_after_the_component_fails_at_host_start(string replace, string property)
    {
        var error = Assert.Throws<OptionsValidationException>(() => new Harness("callback", configure: builder =>
            builder.Services.PostConfigure<CookieAuthenticationOptions>("B", options =>
            {
                if (replace == "events") options.Events = new CookieAuthenticationEvents();
                else if (replace == "type") options.EventsType = typeof(StatefulEvents);
                else options.CookieManager = new ChunkingCookieManager();
            })));
        Assert.Equal("B", error.OptionsName);
        var failure = Assert.Single(error.Failures);
        Assert.Contains($"CookieAuthenticationOptions.{property} for cookie scheme 'B'", failure);
        Assert.Contains("before the AddDistributedTicketStore", failure);
    }

    /// <summary>未接入票据存储的方案不受组合校验约束。</summary>
    [Fact]
    public void Schemes_without_the_ticket_store_are_not_validated()
    {
        using var host = new Harness("callback", configure: builder =>
        {
            builder.Services.AddAuthentication().AddCookie("C");
            builder.Services.PostConfigure<CookieAuthenticationOptions>("C", options =>
            {
                options.Events = new CookieAuthenticationEvents();
                options.EventsType = typeof(StatefulEvents);
                options.CookieManager = new ChunkingCookieManager();
            });
        });
        Assert.IsType<ChunkingCookieManager>(host.Options("C").CookieManager);
    }

    /// <summary>票据缺失只等同未认证：不下发删除 Cookie（免得删掉并发的新登录），重新登录覆盖引用。</summary>
    [Fact]
    public async Task A_missing_ticket_is_unauthenticated_without_deleting_the_cookie_and_login_overwrites_it()
    {
        using var host = new Harness("callback");
        var stale = await host.LoginAsync("A");
        await host.Store.RemoveAsync(Assert.Single(host.Reference(stale, "A").Principal.Claims).Value);
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/me?scheme=A"))
        {
            request.Headers.Add("Cookie", stale);
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
        var fresh = await host.LoginAsync("A", stale);
        Assert.Equal(HttpStatusCode.OK, await host.MeAsync(fresh, "A"));
    }

    /// <summary>缓存读取故障是失败，不被当成缺失票据降级为匿名。</summary>
    [Fact]
    public async Task Cache_read_failures_propagate_instead_of_becoming_anonymous()
    {
        using var host = new Harness("callback");
        var cookie = await host.LoginAsync("A");
        host.Cache.ReadFailure = new IOException("Cache unavailable.");
        await Assert.ThrowsAsync<IOException>(() => host.MeAsync(cookie, "A"));
    }

    [Fact]
    public async Task Missing_expiration_uses_the_fallback_and_an_expired_ticket_is_not_retrievable()
    {
        using var host = new Harness("callback");
        var ticket = host.Ticket();
        var key = await host.Store.StoreAsync(ticket);
        Assert.Equal(host.Clock.GetUtcNow().AddMinutes(5), ticket.Properties.ExpiresUtc);
        Assert.NotNull(await host.Store.RetrieveAsync(key));

        host.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(await host.Store.RetrieveAsync(key));
        await host.Store.RenewAsync(key, ticket);
        Assert.Null(await host.Store.RetrieveAsync(key));
    }

    [Fact]
    public async Task A_renewal_and_deletion_are_serialized_and_late_renewal_cannot_resurrect_the_ticket()
    {
        using var host = new Harness("callback");
        var ticket = host.Ticket();
        var key = await host.Store.StoreAsync(ticket);
        host.Cache.Arm();
        var renewal = host.Store.RenewAsync(key, ticket);
        await host.Cache.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deletion = host.Store.RemoveAsync(key);
        Assert.False(deletion.IsCompleted);
        host.Cache.Release.SetResult();
        await Task.WhenAll(renewal, deletion);
        Assert.Null(await host.Store.RetrieveAsync(key));

        await host.Store.RenewAsync(key, ticket);
        Assert.Null(await host.Store.RetrieveAsync(key));
    }

    /// <summary>
    /// HTTP 重载使用已中止的请求，证明请求中止本身不放宽取消契约；唯一例外（已中止请求的被动续期）
    /// 见 <see cref="An_abandoned_passive_renewal_returns_quietly_and_keeps_the_original_reference_version"/>，
    /// 请求仍在时的续期取消照常抛出。
    /// </summary>
    [Theory]
    [InlineData("store")]
    [InlineData("store-http")]
    [InlineData("read")]
    [InlineData("read-http")]
    [InlineData("renew")]
    [InlineData("renew-http-live")]
    [InlineData("remove")]
    [InlineData("remove-http")]
    public async Task Every_cancelable_native_overload_honors_cancellation(string operation)
    {
        using var host = new Harness("callback");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var context = new DefaultHttpContext { RequestAborted = canceled.Token };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
        {
            "store" => host.Store.StoreAsync(host.Ticket(), canceled.Token),
            "store-http" => host.Store.StoreAsync(host.Ticket(), context, canceled.Token),
            "read" => host.Store.RetrieveAsync("unused", canceled.Token),
            "read-http" => host.Store.RetrieveAsync("unused", context, canceled.Token),
            "renew" => host.Store.RenewAsync("unused", host.Ticket(), canceled.Token),
            "renew-http-live" => host.Store.RenewAsync("unused", host.Ticket(), new DefaultHttpContext(), canceled.Token),
            "remove" => host.Store.RemoveAsync("unused", canceled.Token),
            _ => host.Store.RemoveAsync("unused", context, canceled.Token)
        });
    }

    /// <summary>
    /// 原生处理器在响应启动回调里续期，异常逃出即被 Kestrel 记为应用错误。请求在续期写缓存前中止时，
    /// 续期安静结束：Kestrel 与组件都没有 Error，缓存未被改写，写出的引用仍是原票据版本并能继续认证。
    /// </summary>
    [Fact]
    public async Task An_abandoned_passive_renewal_returns_quietly_and_keeps_the_original_reference_version()
    {
        var cookies = new RecordingCookieManager();
        using var host = new Harness("callback", kestrel: true, configure: builder =>
        {
            builder.Logging.ClearProviders().AddFakeLogging().SetMinimumLevel(LogLevel.Information);
            builder.Services.Configure<CookieAuthenticationOptions>("A", options => options.CookieManager = cookies);
        });
        var logs = host.Services.GetFakeLogCollector();
        var cookie = await host.LoginAsync("A");
        var key = Assert.Single(host.Reference(cookie, "A").Principal.Claims).Value;
        var before = (await host.Cache.GetAsync(key))!;
        host.Clock.Advance(TimeSpan.FromMinutes(11));
        cookies.Appended.Clear();
        logs.Clear();
        host.Cache.Arm();

        // 原始套接字：断开时机由用例决定，不受客户端连接池与重试影响。
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(host.Client.BaseAddress!.Host, host.Client.BaseAddress.Port);
        await socket.SendAsync(Encoding.ASCII.GetBytes(
            $"GET /me?scheme=A HTTP/1.1\r\nHost: {host.Client.BaseAddress.Authority}\r\nCookie: {cookie}\r\n\r\n"));
        await host.Cache.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var finished = WaitForLogAsync(logs, record => record.Category == "Microsoft.AspNetCore.Hosting.Diagnostics" && record.Id.Id == 2);
        socket.Dispose();
        await finished;

        Assert.DoesNotContain(logs.GetSnapshot(), record => record.Level >= LogLevel.Error);
        Assert.Equal(before, await host.Cache.GetAsync(key));
        var reference = Assert.Single(cookies.Appended);
        host.Cache.Disarm();
        Assert.Equal(HttpStatusCode.OK, await host.MeAsync(reference, "A"));
    }

    /// <summary>有效引用的被动续期在调用前请求与调用方令牌均已取消：正常返回，不写缓存。</summary>
    [Fact]
    public async Task A_passive_renewal_canceled_before_the_call_returns_without_writing_the_cache()
    {
        using var host = new Harness("callback");
        var (key, ticket, context) = await host.RenewalTargetAsync(http: true);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        context!.RequestAborted = canceled.Token;
        var before = (await host.Cache.GetAsync(key))!;
        host.Clock.Advance(TimeSpan.FromMinutes(11));

        await host.Store.RenewAsync(key, ticket, context, canceled.Token);

        Assert.Equal(before, await host.Cache.GetAsync(key));
    }

    /// <summary>请求已中止但调用方传入的独立令牌未取消时，依赖抛出的取消是故障，照常传播。</summary>
    [Fact]
    public async Task A_dependency_cancellation_propagates_when_only_the_request_was_aborted()
    {
        using var host = new Harness("callback");
        var (key, ticket, context) = await host.RenewalTargetAsync(http: true);
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        context!.RequestAborted = aborted.Token;
        host.Cache.ReadFailure = new OperationCanceledException("Dependency canceled.");

        await Assert.ThrowsAsync<OperationCanceledException>(() => host.Store.RenewAsync(key, ticket, context, CancellationToken.None));
    }

    /// <summary>显式再次登录时请求已中止，取消照常传播，不把未写入的新版本报告为登录成功。</summary>
    [Fact]
    public async Task Canceling_an_explicit_sign_in_over_an_existing_session_propagates()
    {
        using var host = new Harness("callback");
        var cookie = await host.LoginAsync("A");
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/login-aborted?scheme=A"))
        {
            request.Headers.Add("Cookie", cookie);
            try { using var _ = await host.Client.SendAsync(request); }
            catch (Exception) { /* 服务端中止请求，客户端怎样结束不是本用例关注点 */ }
        }
        Assert.IsAssignableFrom<OperationCanceledException>(await host.Events.AbortedSignIn.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, host.Events.SignedIn);
    }

    /// <summary>失锁取消的是关联令牌而非调用方令牌，服务端与仍在进行的 HTTP 续期都照常抛出。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Losing_a_lock_cancels_a_pending_write_without_mutating_the_stored_ticket(bool http)
    {
        using var loss = new LosingLock();
        using var host = new Harness("callback", loss);
        var (key, ticket, context) = await host.RenewalTargetAsync(http);
        ticket.Properties.Items["changed"] = "uncommitted";
        host.Cache.Arm();
        var pending = context is null ? host.Store.RenewAsync(key, ticket) : host.Store.RenewAsync(key, ticket, context, CancellationToken.None);
        await host.Cache.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        loss.Lose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var stored = (await host.Store.RetrieveAsync(key))!;
        Assert.False(stored.Properties.Items.ContainsKey("changed"));
    }

    /// <summary>调用方未取消时缓存超时抛出的取消是故障，服务端与 HTTP 续期都照常传播。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cache_timeouts_during_renewal_propagate(bool http)
    {
        using var host = new Harness("callback");
        var (key, ticket, context) = await host.RenewalTargetAsync(http);
        host.Cache.ReadFailure = new TaskCanceledException("Cache timeout.");
        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            context is null ? host.Store.RenewAsync(key, ticket) : host.Store.RenewAsync(key, ticket, context, CancellationToken.None));
    }

    [Fact]
    public void Registration_is_idempotent_and_preserves_host_storage()
    {
        using var host = new Harness("callback");
        var services = new ServiceCollection();
        services.AddSingleton(host.Store);
        services.AddDistributedTicketStore("A").AddDistributedTicketStore("A").AddDistributedTicketStore("B");
        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(ITicketStore));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Same(host.Store, descriptor.ImplementationInstance);
        Assert.Single(services, item => item.ServiceType == typeof(IValidateOptions<CookieAuthenticationOptions>));
        Assert.Single(services, item => item.ServiceType == typeof(IPostConfigureOptions<CookieAuthenticationOptions>)
            && ((PostConfigureOptions<CookieAuthenticationOptions>)item.ImplementationInstance!).Name == "A");
    }

    [Fact]
    public void Configuration_binds_before_the_delegate_and_all_schemes_share_the_options()
    {
        using var host = new Harness("callback", configure: builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tickets:KeyPrefix"] = "bound:",
                ["Tickets:FallbackLifetime"] = "00:03:00"
            });
            builder.Services.AddDistributedTicketStore("A", options => options.KeyPrefix = "configured:", "Tickets");
        });
        var options = host.Server.Services.GetRequiredService<IOptions<DistributedTicketStoreOptions>>().Value;
        Assert.Equal("configured:", options.KeyPrefix);
        Assert.Equal(TimeSpan.FromMinutes(3), options.FallbackLifetime);
        Assert.Same(host.Options("A").SessionStore, host.Options("B").SessionStore);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Invalid_storage_options_fail_at_host_start(bool emptyPrefix)
    {
        Assert.Throws<OptionsValidationException>(() => new Harness("callback", configure: builder =>
            builder.Services.AddDistributedTicketStore("A", options =>
            {
                if (emptyPrefix) options.KeyPrefix = " ";
                else options.FallbackLifetime = TimeSpan.Zero;
            })));
    }

    private sealed class Harness : IDisposable
    {
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        public EventState Events { get; } = new();
        private readonly WebApplication _app;
        public TestServer Server => _app.GetTestServer();
        public IServiceProvider Services => _app.Services;
        public HttpClient Client { get; }
        public ITicketStore Store => Services.GetRequiredService<ITicketStore>();
        public CacheProbe Cache => Services.GetRequiredService<CacheProbe>();

        // kestrel：响应启动回调的异常出口由 Kestrel 自己捕获并记录，TestServer 观察不到。
        public Harness(string mode, IDistributedLock? locks = null, Action<WebApplicationBuilder>? configure = null, bool kestrel = false)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            if (kestrel) builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            else builder.WebHost.UseTestServer();
            var services = builder.Services;
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.AddDistributedMemoryCache();
                services.AddSingleton<CacheProbe>();
                services.Replace(ServiceDescriptor.Singleton<IDistributedCache>(provider => provider.GetRequiredService<CacheProbe>()));
                services.AddMemoryLocalLock();
                if (locks is not null) services.Replace(ServiceDescriptor.Singleton(locks));
                services.AddSingleton<TimeProvider>(Clock);
                services.AddSingleton(Events);
                services.AddTransient<StatefulEvents>();
                var auth = services.AddAuthentication("A");
                foreach (var scheme in new[] { "A", "B" })
                {
                    auth.AddCookie(scheme, options =>
                    {
                        options.Cookie.Name = scheme + ".auth";
                        options.TimeProvider = Clock;
                        options.ExpireTimeSpan = TimeSpan.FromMinutes(20);
                        options.SlidingExpiration = true;
                        if (mode == "type") options.EventsType = typeof(StatefulEvents);
                        else if (mode == "subclass") options.Events = new StatefulEvents(Events);
                        else
                        {
                            options.Events.OnSigningIn = context => { Events.SigningIn++; return Task.CompletedTask; };
                            options.Events.OnSignedIn = context => { Events.SignedIn++; return Task.CompletedTask; };
                            options.Events.OnValidatePrincipal = context => { Events.ValidatePrincipal++; return Task.CompletedTask; };
                            options.Events.OnRedirectToLogin = context => { Events.RedirectToLogin++; context.Response.StatusCode = 401; return Task.CompletedTask; };
                        }
                    });
                    services.AddDistributedTicketStore(scheme).AddDistributedTicketStore(scheme);
                }
            }
            configure?.Invoke(builder);
            _app = builder.Build();
            var app = _app;
            {
                app.UseAuthentication();
                app.Run(async context =>
                {
                    var scheme = context.Request.Query["scheme"].FirstOrDefault() ?? "A";
                    if (context.Request.Path == "/both-login")
                    {
                        foreach (var name in new[] { "A", "B" }) await context.SignInAsync(name, Principal(name));
                        context.Response.StatusCode = 204;
                    }
                    else if (context.Request.Path == "/login")
                    {
                        var properties = context.Request.Query.ContainsKey("copy")
                            ? (await context.AuthenticateAsync(scheme)).Properties! : new AuthenticationProperties();
                        properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = "private-token" }]);
                        await context.SignInAsync(scheme, Principal(scheme), properties);
                        context.Response.StatusCode = 204;
                    }
                    else if (context.Request.Path == "/login-aborted")
                    {
                        var properties = (await context.AuthenticateAsync(scheme)).Properties!;
                        context.Abort();
                        try
                        {
                            await context.SignInAsync(scheme, Principal(scheme), properties);
                            Events.AbortedSignIn.TrySetResult(null);
                        }
                        catch (Exception error)
                        {
                            Events.AbortedSignIn.TrySetResult(error);
                        }
                    }
                    else if (context.Request.Path == "/challenge")
                    {
                        await context.ChallengeAsync(scheme);
                    }
                    else if (context.Request.Path == "/forbid")
                    {
                        await context.ForbidAsync(scheme);
                    }
                    else if (context.Request.Path == "/logout")
                    {
                        // 带 RedirectUri 才会走 RedirectToReturnUrl
                        await context.SignOutAsync(scheme, new AuthenticationProperties { RedirectUri = "/signed-out" });
                    }
                    else
                    {
                        var result = await context.AuthenticateAsync(scheme);
                        context.Response.StatusCode = result.Succeeded ? 200 : 401;
                    }
                });
            }
            _app.StartAsync().GetAwaiter().GetResult();
            Client = kestrel
                // 不用客户端 Cookie 容器：用例按请求显式携带引用，容器里的旧 Cookie 会混进请求。
                ? new HttpClient(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) }
                : Server.CreateClient();
        }

        // 已存储的会话：服务端续期直接用票据，HTTP 续期再带上与之匹配、请求仍在进行的 Cookie 引用。
        public async Task<(string Key, AuthenticationTicket Ticket, HttpContext? Context)> RenewalTargetAsync(bool http)
        {
            if (!http)
            {
                var ticket = Ticket();
                return (await Store.StoreAsync(ticket), ticket, null);
            }
            var cookie = await LoginAsync("A");
            var key = Assert.Single(Reference(cookie, "A").Principal.Claims).Value;
            var context = new DefaultHttpContext { RequestServices = Services };
            context.Request.Headers.Cookie = cookie;
            return (key, (await Store.RetrieveAsync(key))!, context);
        }

        public CookieAuthenticationOptions Options(string scheme) =>
            Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme);
        public AuthenticationTicket Reference(string cookie, string scheme) =>
            Options(scheme).TicketDataFormat.Unprotect(Uri.UnescapeDataString(cookie.Split('=', 2)[1]))!;
        public AuthenticationTicket Ticket() => new(Principal("A"), new AuthenticationProperties(), "A");
        public async Task<string> LoginAsync(string scheme, string? cookie = null, bool copy = false)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/login?scheme=" + scheme + (copy ? "&copy=true" : ""));
            if (cookie is not null) request.Headers.Add("Cookie", cookie);
            using var response = await Client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return response.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];
        }
        public async Task<HttpStatusCode> MeAsync(string cookie, string scheme)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/me?scheme=" + scheme);
            request.Headers.Add("Cookie", cookie);
            using var response = await Client.SendAsync(request);
            return response.StatusCode;
        }
        public async Task<HttpStatusCode> ChallengeAsync(string scheme)
        {
            using var response = await Client.GetAsync("/challenge?scheme=" + scheme);
            return response.StatusCode;
        }
        public async Task<HttpStatusCode> LogoutAsync(string cookie, string scheme)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/logout?scheme=" + scheme);
            request.Headers.Add("Cookie", cookie);
            using var response = await Client.SendAsync(request);
            return response.StatusCode;
        }
        public async Task<RedirectContext<CookieAuthenticationOptions>> RedirectContextAsync(string scheme)
        {
            var context = new DefaultHttpContext { RequestServices = Services };
            var definition = (await Services.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync(scheme))!;
            return new RedirectContext<CookieAuthenticationOptions>(context, definition, Options(scheme), new AuthenticationProperties(), "/signed-out");
        }
        private static ClaimsPrincipal Principal(string scheme) => new(new ClaimsIdentity([new Claim("sub", "user")], scheme));
        public void Dispose() { Client.Dispose(); _app.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    public sealed class EventState
    {
        public int SigningIn { get; set; }
        public int SignedIn { get; set; }
        public int ValidatePrincipal { get; set; }
        public int RedirectToLogin { get; set; }
        public List<Guid> InstanceIds { get; } = [];
        public TaskCompletionSource<Exception?> AbortedSignIn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task WaitForLogAsync(FakeLogCollector logs, Func<FakeLogRecord, bool> match)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var record in logs.GetLogsAsync(timeout.Token))
        {
            if (match(record)) return;
        }
    }

    // 记录组件包装之后实际交给内层管理器的 Cookie 值（name=value 形式，可直接回放）。
    private sealed class RecordingCookieManager : ICookieManager
    {
        private readonly ChunkingCookieManager _inner = new();
        public List<string> Appended { get; } = [];
        public string? GetRequestCookie(HttpContext context, string key) => _inner.GetRequestCookie(context, key);
        public void DeleteCookie(HttpContext context, string key, CookieOptions options) => _inner.DeleteCookie(context, key, options);
        public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options)
        {
            Appended.Add(key + "=" + Uri.EscapeDataString(value ?? ""));
            _inner.AppendResponseCookie(context, key, value, options);
        }
    }

    public sealed class StatefulEvents(EventState state) : CookieAuthenticationEvents
    {
        private readonly Guid _id = Guid.NewGuid();
        private bool _signingIn;
        public override Task SigningIn(CookieSigningInContext context)
        {
            _signingIn = true;
            state.SigningIn++;
            return Task.CompletedTask;
        }
        public override Task SignedIn(CookieSignedInContext context)
        {
            Assert.True(_signingIn);
            state.SignedIn++;
            state.InstanceIds.Add(_id);
            return Task.CompletedTask;
        }
        public override Task ValidatePrincipal(CookieValidatePrincipalContext context)
        {
            state.ValidatePrincipal++;
            return Task.CompletedTask;
        }
        public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
        {
            state.RedirectToLogin++;
            context.Response.StatusCode = 401;
            return Task.CompletedTask;
        }
    }

    // 只重写测试关注的五个事件，状态码区别于原生默认的 302
    private sealed class RemainingEventsProbe : CookieAuthenticationEvents
    {
        public Dictionary<string, int> Calls { get; } = [];
        public int Count(string name) => Calls.GetValueOrDefault(name);
        private Task Hit(string name)
        {
            Calls[name] = Count(name) + 1;
            return Task.CompletedTask;
        }
        public override Task CheckSlidingExpiration(CookieSlidingExpirationContext context) => Hit(nameof(CheckSlidingExpiration));
        public override Task SigningOut(CookieSigningOutContext context) => Hit(nameof(SigningOut));
        public override Task RedirectToLogout(RedirectContext<CookieAuthenticationOptions> context) => Hit(nameof(RedirectToLogout));
        public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
        {
            context.Response.StatusCode = 403;
            return Hit(nameof(RedirectToAccessDenied));
        }
        public override Task RedirectToReturnUrl(RedirectContext<CookieAuthenticationOptions> context)
        {
            context.Response.StatusCode = 204;
            return Hit(nameof(RedirectToReturnUrl));
        }
    }

    private sealed class CacheProbe : IDistributedCache
    {
        private readonly MemoryDistributedCache _inner = new(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        private bool _armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? ReadFailure { get; set; }
        public void Arm() => _armed = true;
        public void Disarm() => _armed = false;
        public byte[]? Get(string key) => _inner.Get(key);
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            ReadFailure is { } failure ? throw failure : _inner.GetAsync(key, token);
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _inner.Set(key, value, options);
        public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            if (_armed)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            await _inner.SetAsync(key, value, options, token);
        }
        public void Refresh(string key) => _inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);
        public void Remove(string key) => _inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);
    }

    private sealed class LosingLock : IDistributedLock, IDisposable
    {
        private readonly CancellationTokenSource _lost = new();
        public void Lose() => _lost.Cancel();
        public Task<ILockHandle> LockAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<ILockHandle>(new Handle(_lost.Token));
        public Task<ILockHandle?> TryLockAsync(string key, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult<ILockHandle?>(new Handle(_lost.Token));
        public void Dispose() => _lost.Dispose();
        private sealed class Handle(CancellationToken lost) : ILockHandle
        {
            public CancellationToken LockLost => lost;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
