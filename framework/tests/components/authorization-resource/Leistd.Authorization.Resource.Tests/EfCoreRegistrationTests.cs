using Leistd.Authorization.Resource.Grants;
using Leistd.Authorization.Resource.EntityFrameworkCore;
using Leistd.Authorization.Resource.EntityFrameworkCore.Managers;
using Leistd.Authorization.Resource.EntityFrameworkCore.Stores;
using Leistd.TestBase.Assertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Authorization.Resource.Tests;

/// <summary>
/// <c>AddResourceAuthorizationEfCore</c> 的注册面。理由同权限授予那一侧：
/// 重复调用不该多出描述符，否则 <c>IEnumerable</c> 解析出现重复项，且这种失效不报错。
/// </summary>
public class EfCoreRegistrationTests
{
    private sealed class AclDbContext(DbContextOptions<AclDbContext> options) : DbContext(options);

    private sealed class SecondAclDbContext(DbContextOptions<SecondAclDbContext> options) : DbContext(options);

    [Fact]
    public void Registers_the_store_and_manager_backed_by_the_given_context()
    {
        var services = new ServiceCollection();

        services.AddResourceAuthorizationEfCore<AclDbContext>();

        services.AssertImplementedBy<IResourceGrantStore, EfCoreResourceGrantStore<AclDbContext>>();
        services.AssertImplementedBy<IResourceGrantManager, EfCoreResourceGrantManager<AclDbContext>>();
        // 生命周期一并钉住：两者无状态，按 Transient 登记，上下文共享由 IDbContextProvider 按工作单元保证。
        services.AssertSingle<IResourceGrantStore>(ServiceLifetime.Transient);
        services.AssertSingle<IResourceGrantManager>(ServiceLifetime.Transient);
    }


    // 两个 DbContext 各注册一次：Microsoft DI 不报错，按单服务解析静默取一条——资源 ACL 落错库的症状是越权，而不是报错。
    // TryAdd 只是把"最后一条胜出"换成"第一条胜出"，同样静默，所以必须抛错而不是忽略。
    [Fact]
    public void A_second_context_is_rejected_instead_of_silently_ignored()
    {
        var services = new ServiceCollection();
        services.AddResourceAuthorizationEfCore<AclDbContext>();

        var exception = Record.Exception(() => services.AddResourceAuthorizationEfCore<SecondAclDbContext>());

        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("single authoritative store", exception!.Message);
    }

    // 工厂描述符的 ImplementationType 为 null，代表"宿主自己接了个实现"：同样构成冲突。
    [Fact]
    public void A_host_registered_implementation_is_rejected_as_a_conflict()
    {
        var services = new ServiceCollection();
        services.AddTransient<IResourceGrantStore>(_ => throw new NotSupportedException());

        var exception = Record.Exception(() => services.AddResourceAuthorizationEfCore<AclDbContext>());

        Assert.IsType<InvalidOperationException>(exception);
    }

    // Manager 允许宿主替换（标准 TryAdd 语义），不受"唯一权威存储"断言约束。
    [Fact]
    public void A_host_registered_manager_is_kept_and_not_duplicated()
    {
        var services = new ServiceCollection();
        services.AddScoped<IResourceGrantManager>(_ => throw new NotSupportedException());

        services.AddResourceAuthorizationEfCore<AclDbContext>();

        // 宿主那条连同它的生命周期原样保留
        var descriptor = services.AssertSingle<IResourceGrantManager>(ServiceLifetime.Scoped);
        Assert.NotNull(descriptor.ImplementationFactory);
    }

    // 宿主提前把同一个实现登记成错误的生命周期：必须拒绝——TryAdd 会保留宿主那条错的。
    [Fact]
    public void A_host_registration_with_the_wrong_lifetime_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddScoped<IResourceGrantStore, EfCoreResourceGrantStore<AclDbContext>>();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddResourceAuthorizationEfCore<AclDbContext>());

        Assert.Contains("Scoped", exception.Message);
    }

    [Fact]
    public void Repeated_registration_adds_nothing()
    {
        ServiceCollectionAssertions.AssertIdempotent(services =>
            services.AddResourceAuthorizationEfCore<AclDbContext>());
    }
}
