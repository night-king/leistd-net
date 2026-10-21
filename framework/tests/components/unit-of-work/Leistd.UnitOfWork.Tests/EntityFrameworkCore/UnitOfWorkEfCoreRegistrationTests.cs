using Leistd.TestBase.Assertions;
using Leistd.UnitOfWork.EntityFrameworkCore;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.UnitOfWork.Tests.EntityFrameworkCore;

/// <summary><c>AddUnitOfWorkEfCore</c> 的注册面：生命周期、幂等与宿主提供的上下文来源并存。</summary>
public sealed class UnitOfWorkEfCoreRegistrationTests
{
    // 连接绑定是 Scoped：同一请求内的多个工作单元共用一个绑定，才能拒绝中途改道到另一个库
    [Fact]
    public void Ef_core_registration_uses_the_documented_lifetimes()
    {
        var services = new ServiceCollection();

        services.AddUnitOfWorkEfCore();

        var provider = Assert.Single(services, d => d.ServiceType == typeof(IDbContextProvider<>));
        Assert.Equal((typeof(DbContextProvider<>), ServiceLifetime.Transient), (provider.ImplementationType, provider.Lifetime));
        // 两者是内部类型，按名字定位描述符
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ServiceType.Name == "UnitOfWorkConnectionBinding").Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType.Name == "DbContextConfiguredTargetCache").Lifetime);
    }

    [Fact]
    public void Ef_core_registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddUnitOfWorkEfCore());

    // 宿主为某个上下文提供专用的上下文来源时，封闭泛型注册优先于组件的开放泛型默认值，二者并存
    [Fact]
    public void Host_closed_db_context_provider_coexists_with_the_open_generic_default()
    {
        var services = new ServiceCollection();
        services.AddTransient<IDbContextProvider<HostDbContext>>(_ => throw new NotSupportedException());

        services.AddUnitOfWorkEfCore();

        Assert.NotNull(services.AssertSingle<IDbContextProvider<HostDbContext>>(ServiceLifetime.Transient).ImplementationFactory);
        Assert.Single(services, d => d.ServiceType == typeof(IDbContextProvider<>));
    }

    private sealed class HostDbContext(DbContextOptions<HostDbContext> options) : DbContext(options);
}
