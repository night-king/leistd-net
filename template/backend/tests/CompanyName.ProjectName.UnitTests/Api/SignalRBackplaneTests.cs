#if (IncludeNotifications || IncludeRealTime)
using CompanyName.ProjectName.Api.Hosting;
using CompanyName.ProjectName.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CompanyName.ProjectName.UnitTests.Api;

/// <summary>
/// 背板接线：有 Redis 时所有 Hub 的连接管理换成官方 Redis 实现，通道前缀取锁前缀的最终值；没有 Redis 时保持单节点。
/// </summary>
/// <remarks>
/// 只检查注册结果与解析出的选项，不构造 Redis 连接管理器（它一构造就去连 Redis）。跨节点送达由框架的背板集成测试承担。
/// </remarks>
public sealed class SignalRBackplaneTests
{
    [Fact]
    public void With_redis_every_hub_routes_through_the_redis_backplane_under_the_lock_prefix()
    {
        var services = Compose(new() { ["ConnectionStrings:Redis"] = "localhost:6379,abortConnect=false" });

        Assert.Equal(typeof(RedisHubLifetimeManager<>), LifetimeManagerOf(services));
        Assert.Equal("companyname-projectname:", ChannelPrefixOf(services));
    }

    // 共用一个 Redis 的环境靠覆盖锁前缀区分，背板前缀随之变化
    [Fact]
    public void The_backplane_prefix_follows_a_deployment_override_of_the_lock_prefix()
    {
        var services = Compose(new()
        {
            ["ConnectionStrings:Redis"] = "localhost:6379,abortConnect=false",
            ["Leistd:Lock:Redis:KeyPrefix"] = "companyname-projectname:staging:"
        });

        Assert.Equal("companyname-projectname:staging:", ChannelPrefixOf(services));
    }

    [Fact]
    public void Without_redis_hubs_stay_on_the_single_node_manager()
    {
        var services = Compose([]);

        Assert.Equal(typeof(DefaultHubLifetimeManager<>), LifetimeManagerOf(services));
    }

    // 与 Api 的组合入口同序：Infrastructure 登记缓存与锁，宿主再注册 SignalR 与背板
    private static ServiceCollection Compose(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddMyProjectCacheAndLock(configuration);
        services.AddSignalR().AddMyProjectBackplane(configuration);
        return services;
    }

    private static Type? LifetimeManagerOf(ServiceCollection services) =>
        services.Last(descriptor => descriptor.ServiceType == typeof(HubLifetimeManager<>)).ImplementationType;

    private static string ChannelPrefixOf(ServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<RedisOptions>>().Value.Configuration.ChannelPrefix.ToString();
    }
}
#endif
