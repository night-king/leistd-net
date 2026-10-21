using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Resolution;
using Leistd.MultiTenancy.Stores;
using Leistd.Data.Connections;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.MultiTenancy.Tests.Core;

/// <summary><c>AddMultiTenancyCore()</c> 的注册面：各服务的生命周期、重复调用不叠加、宿主替换口保留。</summary>
public sealed class MultiTenancyCoreRegistrationTests
{
    // 访问器是进程级的 AsyncLocal 单例；解析器按请求缓存结果；其余无状态
    [Fact]
    public void Registration_uses_the_documented_lifetimes()
    {
        var services = new ServiceCollection().AddMultiTenancyCore();

        services.AssertSingle<ICurrentTenantAccessor>(ServiceLifetime.Singleton);
        services.AssertSingle<ICurrentTenant>(ServiceLifetime.Transient);
        services.AssertImplementedBy<ICurrentTenant, CurrentTenant>();
        services.AssertSingle<ITenantNormalizer>(ServiceLifetime.Transient);
        services.AssertSingle<IConnectionAffinityProvider>(ServiceLifetime.Transient);
        services.AssertSingle<ITenantResolver>(ServiceLifetime.Scoped);
        services.AssertImplementedBy<ITenantResolver, TenantResolver>();
        services.AssertSingle<ITenantDatabaseEnumerator>(ServiceLifetime.Transient);
        services.AssertSingle<ITenantDatabaseRunner>(ServiceLifetime.Transient);
    }

    // 不注册存储与连接解析：选哪种实现由宿主决定
    [Fact]
    public void Registration_leaves_store_and_connection_routing_to_the_host()
    {
        var services = new ServiceCollection().AddMultiTenancyCore();

        services.AssertNotRegistered<ITenantStore>();
        services.AssertNotRegistered<IConnectionStringResolver>();
        services.AssertNotRegistered<TenantConnectionRouting>();
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddMultiTenancyCore());

    // 宿主先换掉上下文的存储机制时，组件不得把它覆盖回 AsyncLocal 实现
    [Fact]
    public void A_host_tenant_accessor_is_kept()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentTenantAccessor>(_ => throw new NotSupportedException());

        services.AddMultiTenancyCore();

        Assert.NotNull(services.AssertSingle<ICurrentTenantAccessor>(ServiceLifetime.Singleton).ImplementationFactory);
    }
}
