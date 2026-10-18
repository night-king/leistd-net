using Leistd.RealTime.AspNetCore.SignalR;
using Leistd.RealTime.AspNetCore.SignalR.Hubs;
using Leistd.RealTime.Subscriptions;
using Leistd.RealTime.Tests.TestDoubles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Leistd.RealTime.Tests;

/// <summary>
/// 经真实 dispatcher 的在途调用：断线后才结束的订阅/退订不应被 SignalR 记为调用失败，连接仍在时的取消（依赖或背板超时）仍是故障。
/// </summary>
public sealed class SubscriptionCancellationTests
{
    private const string DispatcherCategory = "Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 调用停在授权或组操作阶段，中止连接（或保持连接）后放行。授权阶段取消时不再尝试入组；组操作阶段的调用已到达组管理器，
    /// 两者在连接已中止时都正常完成、只留 Debug；连接仍在时由 dispatcher 记 Error。
    /// </summary>
    [Theory]
    [InlineData("authorize", true)]
    [InlineData("authorize", false)]
    [InlineData("join", true)]
    [InlineData("join", false)]
    [InlineData("leave", true)]
    [InlineData("leave", false)]
    public async Task Cancellation_after_the_connection_closed_is_not_an_invocation_failure(string stage, bool aborted)
    {
        var gate = new InvocationGate();
        var tracker = new InvocationTracker();
        GatedLifetimeManager? manager = null;
        await using var app = await StartAsync(
            new GatedAuthorizer(stage == "authorize" ? gate : null),
            tracker,
            created => { manager = created; if (stage != "authorize") created.Gate = gate; });
        var logs = app.Services.GetFakeLogCollector();
        await using var client = await TestHubClient.ConnectAsync(app.GetTestServer(), "user-1");

        var call = client.InvokeAsync(stage == "leave" ? "Unsubscribe" : "Subscribe", "catalog:1");
        var token = await gate.Entered.Task.WaitAsync(Wait);
        var caller = await tracker.Caller.Task.WaitAsync(Wait);
        Assert.Equal(caller.ConnectionAborted, token);
        if (aborted) await InvocationTracker.AbortAsync(caller);
        gate.Release.SetResult();
        await tracker.Completed.Task.WaitAsync(Wait);
        await Record.ExceptionAsync(() => call.WaitAsync(Wait));

        var snapshot = logs.GetSnapshot();
        if (aborted)
        {
            Assert.DoesNotContain(snapshot, record => record.Level >= LogLevel.Error);
            Assert.Contains(snapshot, record => record.Category == typeof(RealTimeHub).FullName && record.Level == LogLevel.Debug);
        }
        else
        {
            Assert.Contains(snapshot, record => record.Category == DispatcherCategory && record.Level == LogLevel.Error);
        }

        var groupCalls = manager!.GroupCalls;
        if (stage == "authorize") Assert.Empty(groupCalls);
        else Assert.Equal((stage == "join" ? "add" : "remove", "resource:catalog:1"), (Assert.Single(groupCalls).Action, groupCalls[0].GroupName));
    }

    private static async Task<WebApplication> StartAsync(
        IRealTimeSubscriptionAuthorizer authorizer, InvocationTracker tracker, Action<GatedLifetimeManager> onManager)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders().AddFakeLogging().SetMinimumLevel(LogLevel.Debug);
        builder.Services.AddAuthentication(HeaderAuthenticationHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, HeaderAuthenticationHandler>(HeaderAuthenticationHandler.SchemeName, null);
        builder.Services.AddAuthorization();
        builder.Services.AddRealTimeSignalR();
        builder.Services.AddSingleton(authorizer);
        InvocationTracker.Register(builder.Services, tracker);
        builder.Services.AddSingleton<HubLifetimeManager<RealTimeHub>>(provider =>
        {
            var manager = new GatedLifetimeManager(provider.GetRequiredService<ILogger<DefaultHubLifetimeManager<RealTimeHub>>>());
            onManager(manager);
            return manager;
        });
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRealTimeHub();
        await app.StartAsync();
        return app;
    }
}
