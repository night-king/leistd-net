using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Channels;
using Leistd.AspNetCore.SignalR.Filters;
using Leistd.AspNetCore.SignalR.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.AspNetCore.SignalR.Tests.EndToEnd;

/// <summary>
/// 已建立的 WebSocket 连接上，调用期复评不再通过：连接中止、方法不执行、客户端拿不到结果或流项，
/// 只留本过滤器的 Information，SignalR 的 dispatcher 不把这条预期结局记成 Error。
/// </summary>
/// <remarks>
/// WebSocket 握手之后的调用不再经过 HTTP 授权，只有本过滤器复评（长轮询的每次请求还会先过端点授权，测不到这里）。
/// 有值调用与各种流式调用都经过 <see cref="IHubFilter.InvokeMethodAsync"/>，逐一覆盖；流式调用拿到 null 结果时
/// 官方另记 Debug（EventId 15），不计入断言。
/// </remarks>
public sealed class RevalidationRejectionTests : IAsyncLifetime
{
    private const string HubPolicy = "Hub";
    private const string DispatcherCategory = "Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly PolicySwitch _policy = new();
    private readonly InvocationObserver _observer = new();
    private WebApplication _app = default!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders().AddFakeLogging().SetMinimumLevel(LogLevel.Debug);
        builder.Services.AddAuthentication(EveryoneHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, EveryoneHandler>(EveryoneHandler.SchemeName, null);
        builder.Services.AddAuthorization(options => options.AddPolicy(HubPolicy, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(_ => _policy.Allows)));
        // 观察器先登记，排在被测过滤器外层：被测过滤器拒绝时不调用 next，排在内层的观察器不会运行
        builder.Services.AddSingleton(_observer);
        builder.Services.AddScoped<InvocationObserver.ScopeSignal>();
        builder.Services.Configure<HubOptions>(options => options.AddFilter(_observer));
        builder.Services.AddSignalRAmbientContext();
        builder.Services.Configure<HubIdentityOptions>(options => options.PolicyName = HubPolicy);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapHub<ShapesHub>(ShapesHub.Path).RequireHubAuthorization();
        await _app.StartAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    public static TheoryData<string> Shapes => new() { "Plain", "Value", "Enumerable", "Channel", "ChannelTask" };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Failed_revalidation_closes_the_connection_without_an_invocation_error(string shape)
    {
        await using var connection = await ConnectAsync();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        var logs = _app.Services.GetFakeLogCollector();
        logs.Clear();
        var before = ShapesHub.Executions(shape);
        _policy.Allows = false;

        var outcome = await Record.ExceptionAsync(() => InvokeAsync(connection, shape).WaitAsync(Wait));

        // 客户端以连接关闭结束这次调用，没有收到成功结果或流项
        Assert.NotNull(outcome);
        await closed.Task.WaitAsync(Wait);
        // 调用作用域释放发生在 dispatcher 记完这次调用的日志之后，此时取快照才完整
        await _observer.Completed.Task.WaitAsync(Wait);
        Assert.Equal(before, ShapesHub.Executions(shape));
        var snapshot = logs.GetSnapshot();
        Assert.DoesNotContain(snapshot, record => record.Level >= LogLevel.Error);
        Assert.Contains(snapshot, record =>
            record.Category == typeof(AmbientContextHubFilter).FullName && record.Level == LogLevel.Information);
        Assert.DoesNotContain(snapshot, record => record.Category == DispatcherCategory && record.Id.Id == 8);
    }

    // 对照：复评通过时各形态照常执行并拿到结果，上面的"拒绝"不是因为调用本身打不通
    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Passing_revalidation_runs_the_method(string shape)
    {
        await using var connection = await ConnectAsync();
        var before = ShapesHub.Executions(shape);

        var result = await InvokeAsync(connection, shape).WaitAsync(Wait);

        Assert.Equal(before + 1, ShapesHub.Executions(shape));
        Assert.Equal(shape == "Plain" ? 0 : 3, result);
    }

    /// <summary>按形态发起调用，返回收到的值（流式为项数，普通调用为 0）。</summary>
    private static async Task<int> InvokeAsync(HubConnection connection, string shape)
    {
        switch (shape)
        {
            case "Plain":
                await connection.InvokeAsync(nameof(ShapesHub.Plain));
                return 0;
            case "Value":
                return await connection.InvokeAsync<int>(nameof(ShapesHub.Value));
            default:
                var count = 0;
                await foreach (var _ in connection.StreamAsync<int>(shape))
                {
                    count++;
                }
                return count;
        }
    }

    private async Task<HubConnection> ConnectAsync()
    {
        var server = _app.GetTestServer();
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, ShapesHub.Path), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellationToken) =>
                    await server.CreateWebSocketClient().ConnectAsync(context.Uri, cancellationToken);
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private sealed class PolicySwitch
    {
        public volatile bool Allows = true;
    }

    /// <summary>在调用作用域释放（dispatcher 完成并记完日志之后）时发信号。</summary>
    private sealed class InvocationObserver : IHubFilter
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<object?> InvokeMethodAsync(
            HubInvocationContext invocationContext,
            Func<HubInvocationContext, ValueTask<object?>> next)
        {
            invocationContext.ServiceProvider.GetRequiredService<ScopeSignal>();
            return next(invocationContext);
        }

        internal sealed class ScopeSignal(InvocationObserver observer) : IDisposable
        {
            public void Dispose() => observer.Completed.TrySetResult();
        }
    }

    /// <summary>覆盖 SignalR 的几种调用形态：普通、有值、两种流与异步取得的流。</summary>
    private sealed class ShapesHub : Hub
    {
        public const string Path = "/hubs/shapes";
        private static readonly Dictionary<string, int> Counts = new();

        public static int Executions(string shape)
        {
            lock (Counts)
            {
                return Counts.GetValueOrDefault(shape);
            }
        }

        private static void Executed([CallerMemberName] string shape = "")
        {
            lock (Counts)
            {
                Counts[shape] = Counts.GetValueOrDefault(shape) + 1;
            }
        }

        public Task Plain()
        {
            Executed();
            return Task.CompletedTask;
        }

        public Task<int> Value()
        {
            Executed();
            return Task.FromResult(3);
        }

        public async IAsyncEnumerable<int> Enumerable()
        {
            Executed();
            for (var i = 0; i < 3; i++)
            {
                await Task.Yield();
                yield return i;
            }
        }

        public ChannelReader<int> Channel()
        {
            Executed();
            return Filled();
        }

        public Task<ChannelReader<int>> ChannelTask()
        {
            Executed();
            return Task.FromResult(Filled());
        }

        private static ChannelReader<int> Filled()
        {
            var channel = System.Threading.Channels.Channel.CreateUnbounded<int>();
            for (var i = 0; i < 3; i++)
            {
                channel.Writer.TryWrite(i);
            }
            channel.Writer.Complete();
            return channel.Reader;
        }
    }

    // 每个请求都认证为同一个用户：被测的只是调用期复评
    private sealed class EveryoneHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Everyone";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim("sub", "user-1")], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
