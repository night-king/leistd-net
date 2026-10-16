using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Channels;
using Leistd.Data.Paging;
using Leistd.Notifications.AspNetCore.SignalR;
using Leistd.Notifications.Dtos;
using Leistd.Notifications.Publishing;
using Leistd.Notifications.Stores;
using Leistd.Timing;
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

namespace Leistd.Notifications.Tests.AspNetCore.SignalR;

/// <summary>
/// 不映射 Hub 的宿主（Worker）经正式发布入口发通知：历史照常落库，推送经 Redis 背板送到连在 API 节点上的收件人。
/// </summary>
/// <remarks>
/// API 节点与 Worker 是同一测试进程里的两个独立宿主，共用真实 Redis 与通道前缀。
/// 默认连 <c>127.0.0.1:6379</c>，可用 <c>LEISTD_TEST_REDIS</c> 覆盖；连不上就跳过，CI 以 redis 服务提供。
/// </remarks>
public sealed class NotificationBackplaneTests
{
    private const string TestScheme = "Test";
    private const string UserHeader = "X-Test-User";

    [SkippableFact]
    public async Task A_notification_published_by_a_worker_reaches_the_recipient_on_an_api_node()
    {
        Skip.IfNot(BackplaneRedis.IsAvailable, BackplaneRedis.SkipReason);
        var prefix = $"leistd-test-{Guid.NewGuid():N}:";
        await using var api = await StartApiAsync(prefix);
        var store = new MemoryStore();
        await using var worker = await StartWorkerAsync(prefix, store);
        await using var client = await ConnectAsync(api, "user-1");
        var received = Channel.CreateUnbounded<NotificationOutputDto>();
        client.On<NotificationOutputDto>(NotificationClientMethods.Received, value => received.Writer.TryWrite(value));

        await worker.Services.GetRequiredService<INotificationPublisher>()
            .PublishToUserAsync("user-1", new NotificationInputDto { Title = "Account removed" });

        var notification = await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Account removed", notification.Title);
        var saved = Assert.Single(store.Saved);
        Assert.Equal(("user-1", saved.Notification.Id), (saved.UserId, notification.Id));
    }

    private static async Task<WebApplication> StartApiAsync(string channelPrefix)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(TestScheme).AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(TestScheme, null);
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IClock, UtcClockProvider>();
        builder.Services.AddSingleton<INotificationStore>(new MemoryStore());
        builder.Services.AddNotificationsSignalR();
        AddBackplane(builder.Services, channelPrefix);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapNotificationHub();
        await app.StartAsync();
        return app;
    }

    // 通知 Worker 的最低组合：时钟与唯一的通知存储（实际项目里是 EF 存储及其数据库、工作单元），
    // 选与 API 同一个 Hub 的 SignalR 渠道，接同一背板，不映射 Hub
    private static async Task<RunningWorker> StartWorkerAsync(string channelPrefix, INotificationStore store)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IClock, UtcClockProvider>();
        builder.Services.AddSingleton(store);
        builder.Services.AddNotificationsSignalR();
        AddBackplane(builder.Services, channelPrefix);
        var host = builder.Build();
        await host.StartAsync();
        return new RunningWorker(host);
    }

    private static void AddBackplane(IServiceCollection services, string channelPrefix) =>
        services.AddSignalR().AddStackExchangeRedis(BackplaneRedis.ConnectionString,
            options => options.Configuration.ChannelPrefix = RedisChannel.Literal(channelPrefix));

    private static async Task<HubConnection> ConnectAsync(WebApplication node, string userId)
    {
        var server = node.GetTestServer();
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, Leistd.Notifications.AspNetCore.SignalR.DependencyInjection.DefaultNotificationHubPath).ToString(), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Headers[UserHeader] = userId;
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private sealed class MemoryStore : INotificationStore
    {
        public ConcurrentQueue<(NotificationOutputDto Notification, string UserId)> Saved { get; } = new();

        public Task SaveAsync(NotificationOutputDto notification, string userId, CancellationToken ct = default)
        {
            Saved.Enqueue((notification, userId));
            return Task.CompletedTask;
        }

        public Task<PagedResult<NotificationOutputDto>> GetByUserAsync(
            string userId, PageRequest page, bool unreadOnly = false, CancellationToken ct = default)
            => Task.FromResult(PagedResult<NotificationOutputDto>.Empty);

        public Task<bool> DeleteAsync(string notificationId, string userId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<int> DeleteAllAsync(string userId, CancellationToken ct = default) => Task.FromResult(0);

        public Task MarkAsReadAsync(string notificationId, string userId, CancellationToken ct = default) => Task.CompletedTask;

        public Task MarkAllAsReadAsync(string userId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<int> GetUnreadCountAsync(string userId, CancellationToken ct = default) => Task.FromResult(0);
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
