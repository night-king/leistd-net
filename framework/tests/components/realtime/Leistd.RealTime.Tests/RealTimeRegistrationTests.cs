using Microsoft.AspNetCore.Authorization;
using Leistd.RealTime.Publishing;
using Leistd.RealTime.Subscriptions;
using Leistd.RealTime.AspNetCore.SignalR;
using Leistd.RealTime.AspNetCore.SignalR.Publishing;
using Leistd.TestBase.Assertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Leistd.RealTime.Tests;

/// <summary>实时组件的注册面与端点映射。</summary>
public class RealTimeRegistrationTests
{
    private static IServiceCollection Base() => new ServiceCollection().AddLogging();

    [Fact]
    public void Registration_exposes_the_business_event_publisher()
    {
        var services = Base();

        services.AddRealTimeSignalR();

        services.AssertSingle<IBusinessEventPublisher>(ServiceLifetime.Singleton);
        services.AssertResolvesTo<IBusinessEventPublisher, SignalRBusinessEventPublisher>();
    }

    // 走 SignalR 基座而不是裸 AddSignalR：Hub 方法调用不经中间件，
    // 主体/租户/链路标识与 UserIdentifier 解析全靠基座。
    [Fact]
    public void Registration_brings_in_the_signalr_ambient_context_base()
    {
        var services = Base();

        services.AddRealTimeSignalR();

        services.AssertSingle<IUserIdProvider>(ServiceLifetime.Singleton);
    }

    // 通知与实时常常同时装，两者都会调基座。不幂等会让过滤器挂两遍，
    // 每次 Hub 调用建立两层环境上下文并复评两遍。
    [Fact]
    public void Registration_is_idempotent()
    {
        ServiceCollectionAssertions.AssertIdempotent(services =>
        {
            services.AddLogging();
            services.AddRealTimeSignalR();
        });
    }

    // 授权器缺失必须在映射端点时就失败关闭，并指出该注册什么。
    // 拖到首次订阅才失败太晚，且错误信息离现场很远。
    [Fact]
    public void Mapping_without_an_authorizer_fails_with_actionable_guidance()
    {
        var app = BuildApp(services => services.AddRealTimeSignalR());

        var ex = Assert.Throws<InvalidOperationException>(() => app.MapRealTimeHub());

        Assert.Contains(nameof(IRealTimeSubscriptionAuthorizer), ex.Message);
        Assert.Contains("AddAllowAllRealTimeSubscriptions", ex.Message);
    }

    [Fact]
    public void Mapping_succeeds_once_an_authorizer_is_registered()
    {
        var app = BuildApp(services =>
        {
            services.AddRealTimeSignalR();
            services.AddSingleton<IRealTimeSubscriptionAuthorizer, AllowAllAuthorizer>();
        });

        app.MapRealTimeHub();

        AssertHubMappedAt(app, "/hubs/realtime");
    }

    // 授权器按请求判权限，常常依赖作用域服务；映射时只确认已注册，不在根容器里解析它
    [Fact]
    public void Mapping_accepts_an_authorizer_with_scoped_dependencies_under_scope_validation()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });
        builder.Services.AddRealTimeSignalR();
        builder.Services.AddScoped<ScopedDependency>();
        builder.Services.AddTransient<IRealTimeSubscriptionAuthorizer, ScopedDependencyAuthorizer>();
        builder.Services.AddAuthorization();
        var app = builder.Build();

        app.MapRealTimeHub();

        AssertHubMappedAt(app, "/hubs/realtime");
    }

    private sealed class ScopedDependency;

    private sealed class ScopedDependencyAuthorizer(ScopedDependency dependency) : IRealTimeSubscriptionAuthorizer
    {
        public Task<bool> AuthorizeAsync(RealTimeSubscriptionContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(dependency is not null);
    }

    // 路径由映射处给出，返回官方约定构建器，宿主可以继续链式追加端点约定
    [Fact]
    public void The_hub_maps_at_the_given_pattern_and_accepts_further_conventions()
    {
        var app = BuildApp(services =>
        {
            services.AddRealTimeSignalR();
            services.AddSingleton<IRealTimeSubscriptionAuthorizer, AllowAllAuthorizer>();
        });

        app.MapRealTimeHub("/custom/hub").RequireAuthorization("HubPolicy");

        AssertHubMappedAt(app, "/custom/hub");
        var hub = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .First(endpoint => endpoint.RoutePattern.RawText == "/custom/hub");
        Assert.Contains(hub.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => data.Policy == "HubPolicy");
    }

    private static void AssertHubMappedAt(IEndpointRouteBuilder app, string pattern)
    {
        var endpoints = app.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>();
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == pattern);
    }

    private sealed class AllowAllAuthorizer : IRealTimeSubscriptionAuthorizer
    {
        public Task<bool> AuthorizeAsync(
            RealTimeSubscriptionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private static WebApplication BuildApp(Action<IServiceCollection> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        configure(builder.Services);
        builder.Services.AddAuthorization();
        return builder.Build();
    }

    /// <summary>只放行指定前缀的资源键；前缀按序数比较，大小写不同不算命中。</summary>
    [Theory]
    [InlineData("public:board", true)]
    [InlineData("PUBLIC:board", false)]
    [InlineData("order:42", false)]
    public async Task The_prefix_authorizer_only_allows_listed_prefixes(string resourceKey, bool expected)
    {
        using var provider = new ServiceCollection().AddPrefixRealTimeSubscriptions("public:").BuildServiceProvider();
        var authorizer = provider.GetRequiredService<IRealTimeSubscriptionAuthorizer>();

        var allowed = await authorizer.AuthorizeAsync(new RealTimeSubscriptionContext(resourceKey, "u1"));

        Assert.Equal(expected, allowed);
    }
}
