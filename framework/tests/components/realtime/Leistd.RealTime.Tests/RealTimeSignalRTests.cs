using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Leistd.RealTime.AspNetCore.SignalR;
using Xunit;
using Leistd.RealTime.AspNetCore.SignalR.Hubs;
using Leistd.RealTime.Subscriptions;
using Leistd.RealTime.Publishing;
using Leistd.TestBase.Doubles;
using Leistd.RealTime.Tests.TestDoubles;

namespace Leistd.RealTime.Tests;

public class RealTimeSignalRTests
{
    // 框架不给默认授权器：给不出正确默认值，而"允许任何人订阅任意资源"
    // 必须是宿主明确做出的决定。
    [Fact]
    public void AddRealTime_registers_no_default_subscription_authorizer()
    {
        var sp = new ServiceCollection().AddRealTime().BuildServiceProvider();

        Assert.Null(sp.GetService<IRealTimeSubscriptionAuthorizer>());
    }

    [Fact]
    public async Task AllowAll_is_an_explicit_opt_in_for_public_subscriptions()
    {
        var sp = new ServiceCollection()
            .AddRealTime()
            .AddAllowAllRealTimeSubscriptions()
            .BuildServiceProvider();

        var authorizer = sp.GetRequiredService<IRealTimeSubscriptionAuthorizer>();

        Assert.IsType<AllowAllRealTimeSubscriptionAuthorizer>(authorizer);
        Assert.True(await authorizer.AuthorizeAsync(new RealTimeSubscriptionContext("public-feed", UserId: null)));
    }

    // 没有"关掉校验"的开关：授权器无条件参与每一次订阅。
    [Fact]
    public async Task Subscribe_always_consults_the_authorizer()
    {
        var hub = NewHub(new ThrowingAuthorizer(), out _);

        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.Subscribe("catalog:public"));
    }

    // 组名刻意不带租户段：租户隔离由授权器承担。理由见 RealTimeGroups。
    [Fact]
    public async Task Subscribe_joins_the_resource_group()
    {
        var hub = NewHub(new AllowAllRealTimeSubscriptionAuthorizer(), out var groups);

        await hub.Subscribe("catalog:public");

        Assert.Contains(groups.Added, x => x.GroupName == "resource:catalog:public");
    }

    [Fact]
    public async Task Subscribe_denies_forbidden_resource()
    {
        var hub = NewHub(new DenyingAuthorizer(), out _);

        await Assert.ThrowsAsync<HubException>(() => hub.Subscribe("private-resource"));
    }

    // 背板能据此在断线时提前结束组操作的等待；当前 Redis 背板不使用该令牌，见组件文档。
    [Theory]
    [InlineData("Subscribe")]
    [InlineData("Unsubscribe")]
    public async Task Group_operations_receive_the_connection_aborted_token(string method)
    {
        using var connection = new CancellationTokenSource();
        var hub = NewHub(new AllowAllRealTimeSubscriptionAuthorizer(), out var groups, connection.Token);

        await (method == "Subscribe" ? hub.Subscribe("catalog:public") : hub.Unsubscribe("catalog:public"));

        Assert.Equal(connection.Token, Assert.Single(groups.Tokens));
    }

    // 只有取消在连接已中止时降级；授权拒绝与其他故障即使发生在断线后也照常抛出。
    [Theory]
    [InlineData("denied")]
    [InlineData("subscribe-failure")]
    [InlineData("unsubscribe-failure")]
    public async Task Non_cancellation_failures_propagate_even_after_the_connection_closed(string failure)
    {
        using var connection = new CancellationTokenSource();
        await connection.CancelAsync();
        var hub = NewHub(failure == "denied" ? new DenyingAuthorizer() : new AllowAllRealTimeSubscriptionAuthorizer(), out var groups, connection.Token);
        groups.Failure = new InvalidOperationException("Backplane unavailable.");

        if (failure == "denied") await Assert.ThrowsAsync<HubException>(() => hub.Subscribe("private-resource"));
        else if (failure == "subscribe-failure") await Assert.ThrowsAsync<InvalidOperationException>(() => hub.Subscribe("catalog:public"));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => hub.Unsubscribe("catalog:public"));
    }

    [Fact]
    public void AddRealTimeSignalR_registers_realtime_services_and_leaves_hub_options_to_host()
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddSignalR(hub => hub.KeepAliveInterval = TimeSpan.FromSeconds(3))
            .Services
            .AddRealTimeSignalR()
            .BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<IBusinessEventPublisher>());

        // 宿主用标准方式配的 HubOptions 不被组件覆盖。
        Assert.Equal(TimeSpan.FromSeconds(3), sp.GetRequiredService<IOptions<HubOptions>>().Value.KeepAliveInterval);
    }


    [Fact]
    public async Task BusinessEventPublisher_sends_event_to_resource_group_without_server()
    {
        var clientProxy = new RecordingClientProxy();
        var clients = new RecordingHubClients(clientProxy);
        var publisher = BuildBusinessEventPublisher(new TestHubContext(clients));
        var payload = new TestEvent("updated");

        await publisher.PublishToResourceAsync("product:42", "ProductUpdated", payload);

        Assert.Equal("resource:product:42", clients.LastGroupName);
        var sent = Assert.Single(clientProxy.Sent);
        Assert.Equal("ProductUpdated", sent.Method);
        Assert.Same(payload, Assert.Single(sent.Args));
    }


    private static IBusinessEventPublisher BuildBusinessEventPublisher(IHubContext<RealTimeHub> hubContext)
    {
        var services = new ServiceCollection()
            .AddSingleton(hubContext)
            .AddLogging()
            .AddRealTimeSignalR()
            .BuildServiceProvider();

        return services.GetRequiredService<IBusinessEventPublisher>();
    }

    private sealed record TestEvent(string Value);
    private static RealTimeHub NewHub(
        IRealTimeSubscriptionAuthorizer authorizer,
        out RecordingGroupManager groups,
        CancellationToken connectionAborted = default)
    {
        groups = new RecordingGroupManager();
        return new RealTimeHub(
            new FakeCurrentUser(null),
            authorizer,
            NullLogger<RealTimeHub>.Instance)
        {
            Context = new TestHubCallerContext(connectionId: "conn-test", userIdentifier: "user-1", connectionAborted: connectionAborted),
            Groups = groups
        };
    }


}
