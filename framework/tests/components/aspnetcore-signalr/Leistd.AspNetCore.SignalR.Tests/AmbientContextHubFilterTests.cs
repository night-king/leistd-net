using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Leistd.AmbientContext;
using Leistd.AspNetCore.SignalR.Filters;
using Leistd.AspNetCore.SignalR.Options;
using Leistd.Security;
using Leistd.Security.Claims;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;
using Leistd.TestBase.Doubles;
using Microsoft.Extensions.Time.Testing;
using Leistd.AspNetCore.SignalR.Tests.TestDoubles;

namespace Leistd.AspNetCore.SignalR.Tests;

public class AmbientContextHubFilterTests
{
    private const string DenyPolicy = "deny";

    [Fact]
    public async Task Hub_invocation_runs_inside_the_ambient_context()
    {
        var (filter, provider, context) = Build(Authenticated());
        ClaimsPrincipal? seen = null;

        await filter.InvokeMethodAsync(
            Invocation(provider, context),
            _ =>
            {
                seen = provider.GetRequiredService<ICurrentPrincipalAccessor>().Principal;
                return ValueTask.FromResult<object?>(null);
            });

        Assert.NotNull(seen);
        Assert.Equal(1, provider.GetRequiredService<RecordingContributor>().Entered);
    }

    [Fact]
    public async Task Ambient_context_is_released_after_the_invocation()
    {
        var (filter, provider, context) = Build(Authenticated());

        await filter.InvokeMethodAsync(Invocation(provider, context), _ => ValueTask.FromResult<object?>(null));

        Assert.Null(provider.GetRequiredService<ICurrentPrincipalAccessor>().Principal);
    }

    // 复评必须在上下文之内：宿主的授权 handler 通常读环境态而不是
    // AuthorizationHandlerContext.User。顺序反了会把每个人判成失效并中止连接。
    [Fact]
    public async Task Revalidation_runs_inside_the_ambient_context()
    {
        var (filter, provider, context) = Build(Authenticated());
        var invoked = false;

        await filter.InvokeMethodAsync(
            Invocation(provider, context),
            _ => { invoked = true; return ValueTask.FromResult<object?>(null); });

        Assert.True(invoked);
        Assert.False(context.Aborted);
        Assert.Equal(1, provider.GetRequiredService<RequirePrincipalHandler>().Invocations);
    }

    // 有身份但命名策略不通过：连接中止，方法不执行，也不抛异常（SignalR 会把异常记成调用失败）。
    // 与匿名路径（直接放行）是两回事；经真实 dispatcher 的日志见 EndToEnd/RevalidationRejectionTests。
    [Fact]
    public async Task Failed_policy_aborts_the_connection()
    {
        var (filter, provider, context) = Build(
            Authenticated(),
            new HubIdentityOptions { PolicyName = DenyPolicy });
        var invoked = false;

        var result = await filter.InvokeMethodAsync(
            Invocation(provider, context),
            _ => { invoked = true; return ValueTask.FromResult<object?>("executed"); });

        Assert.Null(result);
        Assert.True(context.Aborted);
        Assert.False(invoked);
    }

    /// <summary>只有后续身份已认证的连接按已认证复评：策略不通过照样中止，不被当成匿名放过。</summary>
    [Fact]
    public async Task A_connection_authenticated_only_by_a_later_identity_is_revalidated()
    {
        var principal = new ClaimsPrincipal(
        [
            new ClaimsIdentity(),
            new ClaimsIdentity([new Claim("sub", "user-1")], authenticationType: "Test")
        ]);
        var (filter, provider, context) = Build(principal, new HubIdentityOptions { PolicyName = DenyPolicy });

        var invoked = false;

        await filter.InvokeMethodAsync(
            Invocation(provider, context),
            _ => { invoked = true; return ValueTask.FromResult<object?>(null); });

        Assert.True(context.Aborted);
        Assert.False(invoked);
    }

    // 匿名连接照样建立环境上下文（链路标识、宿主自定义贡献者仍需要），但没有身份可复评。
    // 传的是"未认证的非 null 空主体"而不是 null：ASP.NET Core 给匿名连接的正是前者，
    // 按 null 建用例会把实现的错误假设一并固化。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Anonymous_connection_gets_context_but_is_not_revalidated(bool nullPrincipal)
    {
        var principal = nullPrincipal ? null : new ClaimsPrincipal(new ClaimsIdentity());
        var (filter, provider, context) = Build(principal);
        var invoked = false;

        await filter.InvokeMethodAsync(
            Invocation(provider, context),
            _ => { invoked = true; return ValueTask.FromResult<object?>(null); });

        Assert.True(invoked);
        Assert.False(context.Aborted);
        Assert.Equal(1, provider.GetRequiredService<RecordingContributor>().Entered);
        Assert.Equal(0, provider.GetRequiredService<RequirePrincipalHandler>().Invocations);
    }

    [Fact]
    public async Task Revalidation_is_throttled_by_the_configured_interval()
    {
        var (filter, provider, context) = Build(
            Authenticated(),
            new HubIdentityOptions { RevalidationInterval = TimeSpan.FromMinutes(5) });

        for (var i = 0; i < 3; i++)
        {
            await filter.InvokeMethodAsync(Invocation(provider, context), _ => ValueTask.FromResult<object?>(null));
        }

        Assert.Equal(1, provider.GetRequiredService<RequirePrincipalHandler>().Invocations);
    }

    // 节流窗口靠注入的时间源计时。推进过窗口后必须再评一次——否则「窗口到期后重新复评」
    // 这件事只能靠真的等 5 分钟来验证，于是通常就不验证了。
    [Fact]
    public async Task Revalidation_runs_again_once_the_throttling_window_has_passed()
    {
        var time = new FakeTimeProvider();
        var (filter, provider, context) = Build(
            Authenticated(),
            new HubIdentityOptions { RevalidationInterval = TimeSpan.FromMinutes(5) },
            time);

        await filter.InvokeMethodAsync(Invocation(provider, context), _ => ValueTask.FromResult<object?>(null));
        await filter.InvokeMethodAsync(Invocation(provider, context), _ => ValueTask.FromResult<object?>(null));
        Assert.Equal(1, provider.GetRequiredService<RequirePrincipalHandler>().Invocations);

        time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await filter.InvokeMethodAsync(Invocation(provider, context), _ => ValueTask.FromResult<object?>(null));

        Assert.Equal(2, provider.GetRequiredService<RequirePrincipalHandler>().Invocations);
    }

    [Fact]
    public async Task Revalidation_runs_on_every_invocation_by_default()
    {
        var (filter, provider, context) = Build(Authenticated());

        for (var i = 0; i < 3; i++)
        {
            await filter.InvokeMethodAsync(Invocation(provider, context), _ => ValueTask.FromResult<object?>(null));
        }

        Assert.Equal(3, provider.GetRequiredService<RequirePrincipalHandler>().Invocations);
    }

    [Fact]
    public async Task Connection_lifetime_callbacks_also_run_inside_the_ambient_context()
    {
        var (filter, provider, context) = Build(Authenticated());
        var hub = new TestHub();
        var lifetime = new HubLifetimeContext(context, provider, hub);
        ClaimsPrincipal? onConnected = null;
        ClaimsPrincipal? onDisconnected = null;

        await filter.OnConnectedAsync(lifetime, _ =>
        {
            onConnected = provider.GetRequiredService<ICurrentPrincipalAccessor>().Principal;
            return Task.CompletedTask;
        });
        await filter.OnDisconnectedAsync(lifetime, exception: null, (_, _) =>
        {
            onDisconnected = provider.GetRequiredService<ICurrentPrincipalAccessor>().Principal;
            return Task.CompletedTask;
        });

        Assert.NotNull(onConnected);
        Assert.NotNull(onDisconnected);
    }

    private static ClaimsPrincipal Authenticated() =>
        new(new ClaimsIdentity([new Claim("sub", "user-1")], authenticationType: "Test"));

    private static HubInvocationContext Invocation(IServiceProvider provider, HubCallerContext context) =>
        new(context, provider, new TestHub(), typeof(TestHub).GetMethod(nameof(TestHub.Ping))!, []);

    private static (AmbientContextHubFilter Filter, ServiceProvider Provider, TestHubCallerContext Context) Build(
        ClaimsPrincipal? principal,
        HubIdentityOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentPrincipalAccessor, CurrentPrincipalAccessor>();
        services.AddAmbientContext();
        services.AddSingleton<RecordingContributor>();
        services.AddSingleton<IAmbientContextContributor>(sp => sp.GetRequiredService<RecordingContributor>());
        services.AddSingleton<RequirePrincipalHandler>();
        services.AddSingleton<IAuthorizationHandler>(sp => sp.GetRequiredService<RequirePrincipalHandler>());
        services.AddSingleton<IAuthorizationHandler, DenyHandler>();
        services.AddAuthorization(o =>
        {
            o.DefaultPolicy = new AuthorizationPolicyBuilder()
                .AddRequirements(new RequirePrincipalRequirement()).Build();
            o.AddPolicy(DenyPolicy, p => p.AddRequirements(new DenyRequirement()));
        });

        var provider = services.BuildServiceProvider();
        var filter = new AmbientContextHubFilter(
            MsOptions.Create(options ?? new HubIdentityOptions()),
            NullLogger<AmbientContextHubFilter>.Instance,
            timeProvider);

        return (filter, provider, new TestHubCallerContext(principal));
    }
}
