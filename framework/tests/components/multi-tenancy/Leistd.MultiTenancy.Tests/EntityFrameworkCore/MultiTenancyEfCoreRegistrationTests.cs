using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.EntityFrameworkCore;
using Leistd.MultiTenancy.EntityFrameworkCore.ConnectionStrings;
using Leistd.MultiTenancy.EntityFrameworkCore.Managers;
using Leistd.MultiTenancy.EntityFrameworkCore.Stores;
using Leistd.MultiTenancy.Stores;
using Leistd.TestBase.Assertions;
using Leistd.Timing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.MultiTenancy.Tests.EntityFrameworkCore;

/// <summary>控制库存储与本地连接解析的注册面：存储按控制库上下文登记为 Transient，重复调用不叠加，宿主实现保留。</summary>
public sealed class MultiTenancyEfCoreRegistrationTests
{
    [Fact]
    public void The_ef_stores_are_transient_and_bound_to_the_given_context()
    {
        var services = new ServiceCollection().AddMultiTenancyEfCore<DbContext>();

        services.AssertSingle<ITenantStore>(ServiceLifetime.Transient);
        services.AssertImplementedBy<ITenantStore, EfCoreTenantStore<DbContext>>();
        services.AssertImplementedBy<ITenantManager, EfCoreTenantManager<DbContext>>();
        services.AssertSingle<ITenantConnectionConfigurationStore>(ServiceLifetime.Transient);
        services.AssertImplementedBy<ITenantConnectionConfigurationStore, EfCoreTenantConnectionConfigurationStore<DbContext>>();
        services.AssertImplementedBy<ITenantConnectionConfigurationManager, EfCoreTenantConnectionConfigurationManager<DbContext>>();
        services.AssertImplementedBy<ITenantConnectionDirectory, EfCoreTenantConnectionDirectory<DbContext>>();
        services.AssertSingle<ITenantDatabaseDirectory>(ServiceLifetime.Transient);
        services.AssertSingle<IClock>(ServiceLifetime.Singleton);
    }

    [Fact]
    public void Registering_the_ef_stores_twice_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddMultiTenancyEfCore<DbContext>());

    // 注册表存储是可替换的（缓存装饰、只读副本）；只有连接配置要求唯一权威来源
    [Fact]
    public void A_host_tenant_store_is_kept()
    {
        var services = new ServiceCollection();
        services.AddTransient<ITenantStore>(_ => throw new NotSupportedException());

        services.AddMultiTenancyEfCore<DbContext>();

        Assert.NotNull(services.AssertSingle<ITenantStore>(ServiceLifetime.Transient).ImplementationFactory);
    }

    [Fact]
    public void Registering_local_resolution_twice_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services =>
            services.AddLocalTenantConnectionResolution<DbContext>(o => o.ControlPlaneConnectionStringName = "Control"));

    // AssertIdempotent 不看验证器：叠加时同一条失败报两遍
    [Fact]
    public void Repeated_local_resolution_keeps_one_options_validator()
    {
        var services = new ServiceCollection()
            .AddLocalTenantConnectionResolution<DbContext>(o => o.ControlPlaneConnectionStringName = "Control")
            .AddLocalTenantConnectionResolution<DbContext>(o => o.ControlPlaneConnectionStringName = "Control");

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<LocalTenantConnectionOptions>));
    }
}
