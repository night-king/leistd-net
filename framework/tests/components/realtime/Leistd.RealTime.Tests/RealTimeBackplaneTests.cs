using Leistd.RealTime.AspNetCore.SignalR;
using Leistd.RealTime.AspNetCore.SignalR.Hubs;
using Leistd.RealTime.Publishing;
using Leistd.RealTime.Subscriptions;
using Leistd.RealTime.Tests.TestDoubles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
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

    /// <summary>
    /// 订阅停在授权阶段，连接中止并从背板移除后再放行：组操作改走跨节点确认，无人确认，约 30 秒后以取消结束。
    /// 调用已到达背板且最终完成，SignalR 不记调用失败，只留 Hub 的 Debug。背板的确认计时器不受应用时钟控制，只能真实等待。
    /// </summary>
    [SkippableFact]
    public async Task A_subscribe_that_outlives_its_connection_ends_on_the_backplane_without_an_invocation_error()
    {
        Skip.IfNot(BackplaneRedis.IsAvailable, BackplaneRedis.SkipReason);
        var gate = new InvocationGate();
        var tracker = new InvocationTracker();
        var observer = new BackplaneObserver();
        await using var b = await StartApiAsync($"leistd-test-{Guid.NewGuid():N}:", builder =>
        {
            builder.Logging.AddFakeLogging().SetMinimumLevel(LogLevel.Debug);
            builder.Services.AddSingleton<IRealTimeSubscriptionAuthorizer>(new GatedAuthorizer(gate, completeAfterRelease: true));
            InvocationTracker.Register(builder.Services, tracker);
            builder.Services.AddSingleton<HubLifetimeManager<RealTimeHub>>(provider =>
                new ObservedLifetimeManager(ActivatorUtilities.CreateInstance<RedisHubLifetimeManager<RealTimeHub>>(provider), observer));
        });
        var logs = b.Services.GetFakeLogCollector();
        await using var client = await TestHubClient.ConnectAsync(b.GetTestServer(), "user-4");

        var call = client.InvokeAsync("Subscribe", "catalog:4");
        await gate.Entered.Task.WaitAsync(Delivery);
        await InvocationTracker.AbortAsync(await tracker.Caller.Task.WaitAsync(Delivery));
        await observer.Disconnected.Task.WaitAsync(Delivery);
        gate.Release.SetResult();
        var groupAction = await observer.GroupAction.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await tracker.Completed.Task.WaitAsync(Delivery);
        await Record.ExceptionAsync(() => call.WaitAsync(Delivery));

        Assert.IsAssignableFrom<OperationCanceledException>(groupAction);
        var snapshot = logs.GetSnapshot();
        Assert.DoesNotContain(snapshot, record => record.Level >= LogLevel.Error);
        Assert.Contains(snapshot, record => record.Category == typeof(RealTimeHub).FullName && record.Level == LogLevel.Debug);
    }

    private static async Task<WebApplication> StartApiAsync(string channelPrefix, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(HeaderAuthenticationHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, HeaderAuthenticationHandler>(HeaderAuthenticationHandler.SchemeName, null);
        builder.Services.AddAuthorization();
        builder.Services.AddRealTimeSignalR();
        AddBackplane(builder.Services, channelPrefix);
        configure?.Invoke(builder);
        builder.Services.AddAllowAllRealTimeSubscriptions();
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

    private static async Task<AsyncHubConnection> ConnectAsync(WebApplication node, string userId) =>
        new(await TestHubClient.ConnectAsync(node.GetTestServer(), userId));

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

    // 转发给 Redis 背板，并报告连接何时已从本节点移除、组操作以什么结束。
    private sealed class BackplaneObserver
    {
        public TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Exception?> GroupAction { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ObservedLifetimeManager(HubLifetimeManager<RealTimeHub> inner, BackplaneObserver observer)
        : HubLifetimeManager<RealTimeHub>, IDisposable
    {
        public override Task OnConnectedAsync(HubConnectionContext connection) => inner.OnConnectedAsync(connection);
        public override async Task OnDisconnectedAsync(HubConnectionContext connection)
        {
            await inner.OnDisconnectedAsync(connection);
            observer.Disconnected.TrySetResult();
        }
        public override async Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            try
            {
                await inner.AddToGroupAsync(connectionId, groupName, cancellationToken);
                observer.GroupAction.TrySetResult(null);
            }
            catch (Exception error)
            {
                observer.GroupAction.TrySetResult(error);
                throw;
            }
        }
        public override Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            inner.RemoveFromGroupAsync(connectionId, groupName, cancellationToken);
        public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = default) =>
            inner.SendAllAsync(methodName, args, cancellationToken);
        public override Task SendAllExceptAsync(string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) =>
            inner.SendAllExceptAsync(methodName, args, excludedConnectionIds, cancellationToken);
        public override Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
            inner.SendConnectionAsync(connectionId, methodName, args, cancellationToken);
        public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
            inner.SendConnectionsAsync(connectionIds, methodName, args, cancellationToken);
        public override Task SendGroupAsync(string groupName, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
            inner.SendGroupAsync(groupName, methodName, args, cancellationToken);
        public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
            inner.SendGroupsAsync(groupNames, methodName, args, cancellationToken);
        public override Task SendGroupExceptAsync(string groupName, string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) =>
            inner.SendGroupExceptAsync(groupName, methodName, args, excludedConnectionIds, cancellationToken);
        public override Task SendUserAsync(string userId, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
            inner.SendUserAsync(userId, methodName, args, cancellationToken);
        public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
            inner.SendUsersAsync(userIds, methodName, args, cancellationToken);
        public void Dispose() => (inner as IDisposable)?.Dispose();
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
