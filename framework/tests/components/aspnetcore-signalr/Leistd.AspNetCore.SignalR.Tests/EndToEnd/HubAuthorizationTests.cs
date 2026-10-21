using System.Security.Claims;
using System.Text.Encodings.Web;
using Leistd.AspNetCore.SignalR.Options;
using Leistd.Notifications.AspNetCore.SignalR;
using Leistd.RealTime;
using Leistd.RealTime.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.AspNetCore.SignalR.Tests.EndToEnd;

/// <summary>组件映射的 Hub 在握手与调用两个阶段按同一个策略授权：<see cref="HubIdentityOptions.PolicyName"/>。</summary>
/// <remarks>
/// <para>握手是一次 HTTP 请求，跑端点上的授权元数据；此后的方法调用不经中间件，由 Hub 过滤器复评。
/// 两边各取一份策略时，宿主指定的策略只在其中一个阶段生效：握手按默认策略拒掉本该放行的主体，
/// 或者调用期放行握手时不会放行的主体。</para>
/// <para>主体用"默认策略拒绝、指定策略允许"构造，才能分辨两个阶段各自用的是哪一个。</para>
/// </remarks>
public sealed class HubAuthorizationTests : IAsyncLifetime
{
    private const string HubPolicy = "Hub";
    private const string RealTimeHubPath = Leistd.RealTime.AspNetCore.SignalR.DependencyInjection.DefaultRealTimeHubPath;
    private const string NotificationHubPath = Leistd.Notifications.AspNetCore.SignalR.DependencyInjection.DefaultNotificationHubPath;

    private readonly PolicySwitch _hubPolicy = new();
    private WebApplication _app = default!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(EveryoneHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, EveryoneHandler>(EveryoneHandler.SchemeName, null);
        builder.Services.AddAuthorization(options =>
        {
            // 主体没有这条 claim：默认策略恒拒绝
            options.DefaultPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireClaim("scope", "default-only")
                .Build();
            options.AddPolicy(HubPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(_ => _hubPolicy.Allows));
        });
        builder.Services.AddRealTimeSignalR();
        builder.Services.AddAllowAllRealTimeSubscriptions();
        builder.Services.AddNotificationsSignalR();
        builder.Services.Configure<HubIdentityOptions>(options => options.PolicyName = HubPolicy);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapRealTimeHub();
        _app.MapNotificationHub();
        await _app.StartAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Theory]
    [InlineData(RealTimeHubPath)]
    [InlineData(NotificationHubPath)]
    public async Task Handshake_is_authorized_by_the_hub_policy_not_the_default_policy(string path)
    {
        await using var connection = Connect(path);

        await connection.StartAsync();

        Assert.Equal(HubConnectionState.Connected, connection.State);
    }

    [Theory]
    [InlineData(RealTimeHubPath)]
    [InlineData(NotificationHubPath)]
    public async Task Handshake_is_rejected_when_the_hub_policy_denies(string path)
    {
        _hubPolicy.Allows = false;
        await using var connection = Connect(path);

        await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());
    }

    // 默认策略恒拒绝：调用能成功，说明复评用的是 Hub 策略；之后 Hub 策略改为拒绝，同一连接上的调用随即被拒
    [Fact]
    public async Task Invocation_is_revalidated_with_the_hub_policy()
    {
        await using var connection = Connect(RealTimeHubPath);
        await connection.StartAsync();

        await connection.InvokeAsync("Unsubscribe", "orders:1");

        _hubPolicy.Allows = false;
        await Assert.ThrowsAnyAsync<Exception>(() => connection.InvokeAsync("Unsubscribe", "orders:1"));
    }

    private HubConnection Connect(string path)
    {
        var server = _app.GetTestServer();
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, path), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .Build();
    }

    private sealed class PolicySwitch
    {
        public volatile bool Allows = true;
    }

    // 每个请求都认证为同一个用户：被测的只是授权，认证不是变量
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
