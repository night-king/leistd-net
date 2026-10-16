using System.Security.Claims;
using System.Text.Encodings.Web;
using Leistd.RealTime.AspNetCore.SignalR;
using Leistd.RealTime.AspNetCore.SignalR.Hubs;
using Leistd.RealTime.Publishing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace Leistd.RealTime.Tests;

/// <summary>
/// 多节点与非 Hub 进程发布：节点之间经 SignalR 官方 Redis 背板路由，发布方不管连接落在哪个进程。
/// </summary>
/// <remarks>
/// 两个映射了 Hub 的 API 节点加一个不映射 Hub 的通用宿主（Worker），共用同一 Redis 与通道前缀；客户端只连 B。
/// 默认连 <c>127.0.0.1:6379</c>，可用 <c>LEISTD_TEST_REDIS</c> 覆盖；连不上就跳过，CI 以 redis 服务提供。
/// </remarks>
public sealed class RealTimeBackplaneTests
{
    private const string TestScheme = "Test";
    private const string UserHeader = "X-Test-User";
    private static readonly TimeSpan Delivery = TimeSpan.FromSeconds(10);

    [SkippableFact]
    public async Task Resource_events_from_another_node_and_from_a_worker_reach_the_subscriber()
    {
        Skip.IfNot(BackplaneRedis.IsAvailable, BackplaneRedis.SkipReason);
        var prefix = $"leistd-test-{Guid.NewGuid():N}:";
        await using var a = await StartApiAsync(prefix);
        await using var b = await StartApiAsync(prefix);
        await using var worker = await StartWorkerAsync(prefix);
        await using var client = await ConnectAsync(b, "user-1");
        var received = Collect<Payload>(client, "Updated");
        await client.InvokeAsync("Subscribe", "catalog:1");

        await a.Services.GetRequiredService<IBusinessEventPublisher>().PublishToResourceAsync("catalog:1", "Updated", new Payload("from-a"));
        Assert.Equal("from-a", (await received.ReadAsync().AsTask().WaitAsync(Delivery)).Value);

        await worker.Services.GetRequiredService<IBusinessEventPublisher>().PublishToResourceAsync("catalog:1", "Updated", new Payload("from-worker"));
        Assert.Equal("from-worker", (await received.ReadAsync().AsTask().WaitAsync(Delivery)).Value);
    }

    // 按用户寻址（通知渠道走这条路）同样跨节点、跨进程
    [SkippableFact]
    public async Task User_messages_from_a_worker_reach_the_users_connection_on_another_node()
    {
        Skip.IfNot(BackplaneRedis.IsAvailable, BackplaneRedis.SkipReason);
        var prefix = $"leistd-test-{Guid.NewGuid():N}:";
        await using var b = await StartApiAsync(prefix);
        await using var worker = await StartWorkerAsync(prefix);
        await using var client = await ConnectAsync(b, "user-2");
        var received = Collect<Payload>(client, "Notified");

        await worker.Services.GetRequiredService<IHubContext<RealTimeHub>>().Clients.User("user-2")
            .SendAsync("Notified", new Payload("to-user"));
        Assert.Equal("to-user", (await received.ReadAsync().AsTask().WaitAsync(Delivery)).Value);
    }

    // 前缀不同就是两个互不相通的应用（或环境），共用同一 Redis 也不串推；同前缀的第三个节点照常送达，作为正向对照
    [SkippableFact]
    public async Task Nodes_with_different_channel_prefixes_do_not_see_each_others_events()
    {
        Skip.IfNot(BackplaneRedis.IsAvailable, BackplaneRedis.SkipReason);
        var prefix = $"leistd-test-{Guid.NewGuid():N}:";
        await using var other = await StartApiAsync($"leistd-test-{Guid.NewGuid():N}:");
        await using var b = await StartApiAsync(prefix);
        await using var sameApp = await StartApiAsync(prefix);
        await using var client = await ConnectAsync(b, "user-3");
        var received = Collect<Payload>(client, "Updated");
        await client.InvokeAsync("Subscribe", "catalog:3");

        await other.Services.GetRequiredService<IBusinessEventPublisher>().PublishToResourceAsync("catalog:3", "Updated", new Payload("leak"));
        await sameApp.Services.GetRequiredService<IBusinessEventPublisher>().PublishToResourceAsync("catalog:3", "Updated", new Payload("same-app"));
        Assert.Equal("same-app", (await received.ReadAsync().AsTask().WaitAsync(Delivery)).Value);
        await Assert.ThrowsAsync<TimeoutException>(() => received.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
    }

    private static async Task<WebApplication> StartApiAsync(string channelPrefix)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(TestScheme).AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(TestScheme, null);
        builder.Services.AddAuthorization();
        builder.Services.AddRealTimeSignalR().AddAllowAllRealTimeSubscriptions();
        AddBackplane(builder.Services, channelPrefix);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRealTimeHub();
        await app.StartAsync();
        return app;
    }

    // Worker：通用宿主，不映射任何 Hub，只注册发布所需的服务与同一背板
    private static async Task<RunningWorker> StartWorkerAsync(string channelPrefix)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddRealTimeSignalR();
        AddBackplane(builder.Services, channelPrefix);
        var host = builder.Build();
        await host.StartAsync();
        return new RunningWorker(host);
    }

    private static void AddBackplane(IServiceCollection services, string channelPrefix) =>
        services.AddSignalR().AddStackExchangeRedis(BackplaneRedis.ConnectionString,
            options => options.Configuration.ChannelPrefix = RedisChannel.Literal(channelPrefix));

    private static async Task<AsyncHubConnection> ConnectAsync(WebApplication node, string userId)
    {
        var server = node.GetTestServer();
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, Leistd.RealTime.AspNetCore.SignalR.DependencyInjection.DefaultRealTimeHubPath).ToString(), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Headers[UserHeader] = userId;
            })
            .Build();
        await connection.StartAsync();
        return new AsyncHubConnection(connection);
    }

    private static System.Threading.Channels.ChannelReader<T> Collect<T>(AsyncHubConnection connection, string method)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<T>();
        connection.Inner.On<T>(method, value => channel.Writer.TryWrite(value));
        return channel.Reader;
    }

    private sealed record Payload(string Value);

    private sealed class AsyncHubConnection(HubConnection inner) : IAsyncDisposable
    {
        public HubConnection Inner { get; } = inner;

        public Task InvokeAsync(string method, string argument) => Inner.InvokeAsync(method, argument);

        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var user) || string.IsNullOrEmpty(user))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim("sub", user!)], TestScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }

    private static class BackplaneRedis
    {
        private const string ConnectionEnvironmentVariable = "LEISTD_TEST_REDIS";

        public static string ConnectionString =>
            Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable) is { Length: > 0 } value ? value : "127.0.0.1:6379";

        // 不回显连接串：它可能带口令
        public const string SkipReason =
            "No Redis is reachable. Start one (docker run -d -p 6379:6379 redis:8-alpine) or set " + ConnectionEnvironmentVariable + ".";

        private static readonly Lazy<bool> Available = new(() =>
        {
            try
            {
                using var connection = ConnectionMultiplexer.Connect(ConnectionString + ",connectTimeout=1000,abortConnect=true");
                return connection.IsConnected;
            }
            catch (RedisConnectionException)
            {
                return false;
            }
        });

        public static bool IsAvailable => Available.Value;
    }

    // 失败路径也要异步停下 Worker：await using 保证在断言失败、超时后照样执行
    private sealed class RunningWorker(IHost host) : IAsyncDisposable
    {
        public IServiceProvider Services => host.Services;

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}
