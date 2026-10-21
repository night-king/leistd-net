using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.EntityFrameworkCore;
using Leistd.MultiTenancy.EntityFrameworkCore.ConnectionStrings;
using Leistd.Data.Connections;
using Leistd.TestBase.Assertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Leistd.MultiTenancy.Tests.TestDoubles;

namespace Leistd.MultiTenancy.Tests.Core;

public class RegistrationAndOptionsTests
{
    [Fact]
    public void Remote_resolution_registers_a_scoped_resolver_and_a_hybrid_cache()
    {
        var services = new ServiceCollection().AddRemoteTenantConnectionResolution();

        services.AssertSingle<IConnectionStringResolver>(ServiceLifetime.Scoped);
        services.AssertImplementedBy<IConnectionStringResolver, RemoteConnectionStringResolver>();
        services.AssertSingle<Microsoft.Extensions.Caching.Hybrid.HybridCache>(ServiceLifetime.Singleton);
        services.AssertImplementedBy<ITenantMigrationTargetProvider, TenantMigrationTargetProvider>();
    }

    [Fact]
    public void Registering_remote_resolution_twice_is_idempotent()
    {
        ServiceCollectionAssertions.AssertIdempotent(services => services.AddRemoteTenantConnectionResolution());
    }

    [Fact]
    public void A_resolver_registered_by_the_host_is_kept()
    {
        var services = new ServiceCollection();
        services.AddScoped<IConnectionStringResolver, HostResolver>();

        services.AddRemoteTenantConnectionResolution();

        services.AssertImplementedBy<IConnectionStringResolver, HostResolver>();
    }

    [Fact]
    public void Local_resolution_registers_the_control_context_variant()
    {
        var services = new ServiceCollection()
            .AddLocalTenantConnectionResolution<ControlDbContext>(o => o.ControlPlaneConnectionStringName = "Control");

        services.AssertSingle<IConnectionStringResolver>(ServiceLifetime.Scoped);
        services.AssertImplementedBy<IConnectionStringResolver, LocalConnectionStringResolver<ControlDbContext>>();
        services.AssertImplementedBy<ITenantMigrationTargetProvider, TenantMigrationTargetProvider>();
    }

    /// <summary>两个租户路由入口都要放下路由标记，逐库枚举只认它。</summary>
    /// <remarks>
    /// 没有这条，标记就成了"我以为注册了"——而漏放的后果是逐库作业按单库走，
    /// 每一个独立库被永久跳过还报成功。只装 <c>AddMultiTenancyCore()</c>（不分库）时必须没有它。
    /// </remarks>
    [Theory]
    [InlineData("local", true)]
    [InlineData("remote", true)]
    [InlineData("core-only", false)]
    public void Only_the_tenant_routing_entry_points_register_the_routing_marker(string registration, bool expected)
    {
        var services = new ServiceCollection().AddMultiTenancyCore();
        switch (registration)
        {
            case "local":
                services.AddLocalTenantConnectionResolution<ControlDbContext>(o => o.ControlPlaneConnectionStringName = "Control");
                break;
            case "remote":
                services.AddRemoteTenantConnectionResolution();
                break;
        }

        Assert.Equal(expected, services.Any(service => service.ServiceType == typeof(TenantConnectionRouting)));
    }

    /// <summary>两个解析入口单独使用即自闭环：解析器依赖的当前租户由入口自己登记。</summary>
    /// <remarks>
    /// 迁移作业只注册持久化、不经 Web 集成；开发环境的宿主在构建期校验依赖，缺 <c>ICurrentTenant</c> 就起不来。
    /// 这里只补宿主本就要给的前提：配置、日志、连接配置存储，本地解析另加控制库与密钥环。
    /// </remarks>
    [Theory]
    [InlineData("local")]
    [InlineData("remote")]
    public void Each_resolution_entry_point_is_self_contained(string registration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<ITenantConnectionConfigurationStore>(new ScriptedRemoteSource());
        if (registration == "local")
        {
            services.AddDbContext<ControlDbContext>(options => options.UseSqlite("DataSource=:memory:"));
            services.AddDataProtection();
            services.AddLocalTenantConnectionResolution<ControlDbContext>(o => o.ControlPlaneConnectionStringName = "Control");
        }
        else
        {
            services.AddRemoteTenantConnectionResolution();
        }

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    // 远端宿主不持有控制面的密钥环：远端解析的注册面里不能冒出任何 Data Protection 依赖
    [Fact]
    public void Remote_resolution_does_not_require_a_data_protection_key_ring()
    {
        using var host = new RemoteHost();

        Assert.NotNull(host.Provider.GetRequiredService<IServiceScopeFactory>().CreateScope()
            .ServiceProvider.GetRequiredService<IConnectionStringResolver>());
    }

    // 未配置时取默认值：远端解析不应为了一个有默认意义的 TTL 多一个必填项
    [Fact]
    public void An_unset_cache_lifetime_falls_back_to_the_default()
    {
        using var host = new RemoteHost(cacheLifetime: null);

        Assert.Equal(TenantRouteCacheOptions.DefaultCacheLifetime,
            host.Provider.GetRequiredService<IOptions<TenantRouteCacheOptions>>().Value.CacheLifetime);
    }

    // 越界值启动即失败：它决定改路由前的排空等待
    [Theory]
    [InlineData("00:00:00")]
    [InlineData("01:00:01")]
    public void An_unusable_cache_lifetime_fails_validation(string? lifetime)
    {
        using var host = new RemoteHost(cacheLifetime: lifetime);

        Assert.Throws<OptionsValidationException>(
            () => host.Provider.GetRequiredService<IOptions<TenantRouteCacheOptions>>().Value);
    }

    [Fact]
    public void The_cache_lifetime_binds_from_the_multi_tenancy_routing_section()
    {
        using var host = new RemoteHost(cacheLifetime: "00:02:30");

        Assert.Equal(TimeSpan.FromSeconds(150),
            host.Provider.GetRequiredService<IOptions<TenantRouteCacheOptions>>().Value.CacheLifetime);
    }

    // 控制库必须钉在自己的连接名上，否则它会被当成租户数据按租户路由
    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("Default")]
    public void The_control_plane_name_is_required_and_not_Default(string? name)
    {
        using var provider = new ServiceCollection()
            .AddLocalTenantConnectionResolution<ControlDbContext>(o => o.ControlPlaneConnectionStringName = name)
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<LocalTenantConnectionOptions>>().Value);
    }

    private sealed class HostResolver : IConnectionStringResolver
    {
        public Task<string> ResolveAsync(string connectionStringName, CancellationToken cancellationToken = default)
            => Task.FromResult("host");
    }

    private sealed class ControlDbContext(DbContextOptions<ControlDbContext> options) : DbContext(options);
}
