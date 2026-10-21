using System.Security.Claims;
using System.Text.Encodings.Web;
using Leistd.RealTime.AspNetCore.SignalR.Hubs;
using Leistd.RealTime.Subscriptions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Leistd.RealTime.Tests.TestDoubles;

/// <summary>按请求头 <see cref="UserHeader"/> 建立已认证主体的测试认证方案。</summary>
internal sealed class HeaderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var user) || string.IsNullOrEmpty(user))
            return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim("sub", user!)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>经 TestServer 以长轮询连上业务事件 Hub 的真实客户端。</summary>
internal static class TestHubClient
{
    public static async Task<HubConnection> ConnectAsync(TestServer server, string userId)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, Leistd.RealTime.AspNetCore.SignalR.DependencyInjection.DefaultRealTimeHubPath).ToString(), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Headers[HeaderAuthenticationHandler.UserHeader] = userId;
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }
}

/// <summary>让在途调用停在某个阶段：进入时交出收到的令牌，等用例放行后再继续。</summary>
internal sealed class InvocationGate
{
    public TaskCompletionSource<CancellationToken> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task PassAsync(CancellationToken cancellationToken)
    {
        Entered.TrySetResult(cancellationToken);
        await Release.Task;
    }
}

/// <summary>
/// 授权器：无门时直接放行；有门时停住，放行后连接已中止则抛调用方取消，否则模拟依赖超时。
/// <c>completeAfterRelease</c> 时放行后不看令牌直接允许，让调用带着已中止的连接继续走到组操作。
/// </summary>
internal sealed class GatedAuthorizer(InvocationGate? gate = null, bool completeAfterRelease = false) : IRealTimeSubscriptionAuthorizer
{
    public async Task<bool> AuthorizeAsync(RealTimeSubscriptionContext context, CancellationToken cancellationToken = default)
    {
        if (gate is null) return true;
        await gate.PassAsync(cancellationToken);
        if (completeAfterRelease) return true;
        cancellationToken.ThrowIfCancellationRequested();
        throw new OperationCanceledException("Simulated authorization timeout.");
    }
}

/// <summary>
/// 内存生命周期管理器：有门的组操作停住，放行后像 Redis 背板那样不看令牌、以确认超时（<see cref="TaskCanceledException"/>）结束。
/// </summary>
internal sealed class GatedLifetimeManager(ILogger<DefaultHubLifetimeManager<RealTimeHub>> logger) : DefaultHubLifetimeManager<RealTimeHub>(logger)
{
    public InvocationGate? Gate { get; set; }
    public List<(string Action, string GroupName, CancellationToken Token)> GroupCalls { get; } = [];

    public override Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        GroupActionAsync("add", groupName, cancellationToken, () => base.AddToGroupAsync(connectionId, groupName, cancellationToken));

    public override Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        GroupActionAsync("remove", groupName, cancellationToken, () => base.RemoveFromGroupAsync(connectionId, groupName, cancellationToken));

    private async Task GroupActionAsync(string action, string groupName, CancellationToken cancellationToken, Func<Task> inner)
    {
        lock (GroupCalls) GroupCalls.Add((action, groupName, cancellationToken));
        if (Gate is null)
        {
            await inner();
            return;
        }
        await Gate.PassAsync(cancellationToken);
        throw new TaskCanceledException("Simulated acknowledgement timeout.");
    }
}

/// <summary>记录每次 Hub 调用的调用方上下文，并在调用作用域释放（dispatcher 记完结果日志之后）时发信号。</summary>
internal sealed class InvocationTracker : IHubFilter
{
    public TaskCompletionSource<HubCallerContext> Caller { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        invocationContext.ServiceProvider.GetRequiredService<ScopeSignal>();
        Caller.TrySetResult(invocationContext.Context);
        return next(invocationContext);
    }

    public static void Register(IServiceCollection services, InvocationTracker tracker)
    {
        services.AddSingleton(tracker);
        services.AddScoped<ScopeSignal>();
        services.AddSignalR(options => options.AddFilter(tracker));
    }

    /// <summary>中止调用方连接，并等到 <see cref="HubCallerContext.ConnectionAborted"/> 实际取消（官方实现异步取消）。</summary>
    public static async Task AbortAsync(HubCallerContext caller)
    {
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = caller.ConnectionAborted.Register(() => aborted.TrySetResult());
        caller.Abort();
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    internal sealed class ScopeSignal(InvocationTracker tracker) : IDisposable
    {
        public void Dispose() => tracker.Completed.TrySetResult();
    }
}
