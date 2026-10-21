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
/// 经真实 dispatcher 的订阅结果：被授权器拒绝是预期结局，以返回值 false 告知客户端、记 Warning，
/// 不以异常结束调用（SignalR 会把异常记成 Error 级的调用失败）；授权通过且入组完成才返回 true。
/// </summary>
public sealed class SubscriptionRejectionTests
{
    private const string DispatcherCategory = "Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Denied_subscription_returns_false_and_logs_a_warning_without_an_invocation_error()
    {
        var tracker = new InvocationTracker();
        GatedLifetimeManager? manager = null;
        await using var app = await StartAsync(new DenyingAuthorizer(), tracker, created => manager = created);
        var logs = app.Services.GetFakeLogCollector();
        await using var client = await TestHubClient.ConnectAsync(app.GetTestServer(), "user-1");

        var accepted = await client.InvokeAsync<bool>("Subscribe", "private-resource").WaitAsync(Wait);
        await tracker.Completed.Task.WaitAsync(Wait);

        Assert.False(accepted);
        Assert.Empty(manager!.GroupCalls);
        var snapshot = logs.GetSnapshot();
        Assert.DoesNotContain(snapshot, record => record.Level >= LogLevel.Error);
        Assert.DoesNotContain(snapshot, record => record.Category == DispatcherCategory && record.Id.Id == 8);
        var warning = Assert.Single(snapshot, record =>
            record.Category == typeof(RealTimeHub).FullName && record.Level == LogLevel.Warning);
        Assert.Null(warning.Exception);
        // 连接仍可用：被拒的只是这一个资源
        Assert.Equal(HubConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task Allowed_subscription_returns_true_after_joining_the_group()
    {
        var tracker = new InvocationTracker();
        GatedLifetimeManager? manager = null;
        await using var app = await StartAsync(new AllowAllRealTimeSubscriptionAuthorizer(), tracker, created => manager = created);
        await using var client = await TestHubClient.ConnectAsync(app.GetTestServer(), "user-1");

        var accepted = await client.InvokeAsync<bool>("Subscribe", "catalog:1").WaitAsync(Wait);

        Assert.True(accepted);
        Assert.Equal(("add", "resource:catalog:1"), (Assert.Single(manager!.GroupCalls).Action, manager.GroupCalls[0].GroupName));
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
