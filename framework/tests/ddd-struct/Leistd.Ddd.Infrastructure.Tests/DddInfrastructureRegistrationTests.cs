using Leistd.Data.EntityFrameworkCore.Querying;
using Leistd.Data.Querying;
using Leistd.Data.Filters;
using Leistd.Ddd.Infrastructure.EventBus;
using Leistd.Ddd.Infrastructure.Persistence.Interceptors;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Ddd.Infrastructure.Tests;

/// <summary><c>AddDddInfrastructure</c> 的注册面：生命周期、幂等，以及宿主先注册的实现保留。</summary>
/// <remarks>
/// 数据过滤器的状态在 AsyncLocal 里，按单例共享；领域事件拦截器的待发布事件按 DbContext 暂存在静态弱表里，
/// 拦截器本身无状态，按 Transient 登记。宿主重复调用（组合根拆分、测试宿主再调一次）不得多出描述符，
/// 先注册的查询执行器与过滤器（测试替身、宿主扩展）也不得被基座默认值盖掉。
/// </remarks>
public sealed class DddInfrastructureRegistrationTests
{
    [Fact]
    public void Registration_uses_the_documented_lifetimes()
    {
        var services = new ServiceCollection();

        services.AddDddInfrastructure();

        services.AssertSingle<IQueryableAsyncExecuter>(ServiceLifetime.Singleton);
        services.AssertImplementedBy<IQueryableAsyncExecuter, EfCoreQueryableAsyncExecuter>();
        services.AssertSingle<IDataFilter>(ServiceLifetime.Singleton);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(IDataFilter<>)).Lifetime);
        services.AssertSingle<LocalEventSaveChangesInterceptor>(ServiceLifetime.Transient);
        services.AssertSingle<ConcurrencyStampSaveChangesInterceptor>(ServiceLifetime.Singleton);
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddDddInfrastructure());

    [Fact]
    public void Host_registered_executer_and_data_filters_are_kept()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IQueryableAsyncExecuter>(_ => throw new NotSupportedException());
        services.AddSingleton<IDataFilter>(_ => throw new NotSupportedException());
        services.AddSingleton(typeof(IDataFilter<>), typeof(HostDataFilter<>));

        services.AddDddInfrastructure();

        Assert.NotNull(services.AssertSingle<IQueryableAsyncExecuter>(ServiceLifetime.Singleton).ImplementationFactory);
        Assert.NotNull(services.AssertSingle<IDataFilter>(ServiceLifetime.Singleton).ImplementationFactory);
        Assert.Equal(typeof(HostDataFilter<>), Assert.Single(services, d => d.ServiceType == typeof(IDataFilter<>)).ImplementationType);
    }

    private sealed class HostDataFilter<TFilter> : DataFilter<TFilter>
        where TFilter : class;
}
