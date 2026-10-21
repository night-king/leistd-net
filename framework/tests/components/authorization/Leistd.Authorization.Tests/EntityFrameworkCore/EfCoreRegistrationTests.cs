using Leistd.Authorization.Grants;
using Leistd.Authorization.EntityFrameworkCore;
using Leistd.Authorization.EntityFrameworkCore.Managers;
using Leistd.Authorization.EntityFrameworkCore.Stores;
using Leistd.TestBase.Assertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Authorization.Tests.EntityFrameworkCore;

/// <summary>
/// <c>AddPermissionAuthorizationEfCore</c> 的注册面。
///
/// 宿主组合根拆开之后重复调用 <c>AddXxx()</c> 是常态，而这里的失效是静默的：
/// 存储与管理器各多出一条描述符，按 <c>IEnumerable</c> 解析时出现重复项。
/// 授权面的错还有一层——两个 DbContext 各注册一次时按顺序取最后一条，
/// 权限授予会落到宿主没预期的那个库，读回来的也是那一个，全程不报错。
/// </summary>
public class EfCoreRegistrationTests
{
    private sealed class FirstDbContext(DbContextOptions<FirstDbContext> options) : DbContext(options);

    private sealed class SecondDbContext(DbContextOptions<SecondDbContext> options) : DbContext(options);



    [Fact]
    public void Registers_the_store_and_manager_backed_by_the_given_context()
    {
        var services = new ServiceCollection();

        services.AddPermissionAuthorizationEfCore<FirstDbContext>();

        services.AssertImplementedBy<IPermissionGrantStore, EfCorePermissionGrantStore<FirstDbContext>>();
        services.AssertImplementedBy<IPermissionGrantManager, EfCorePermissionGrantManager<FirstDbContext>>();
        // 生命周期一并钉住：只验实现类型的话，transient 被误改成 scoped 不会红——
        // 而那会改变它与工作单元/DbContext 的共享关系。
        services.AssertSingle<IPermissionGrantStore>(ServiceLifetime.Transient);
        services.AssertSingle<IPermissionGrantManager>(ServiceLifetime.Transient);
    }


    // 两个 DbContext 各注册一次：Microsoft DI 不报错，按单服务解析静默取一条——权限授予会写进/读自宿主没预期的那个库，症状是越权或全员 403。
    // TryAdd 只是把"最后一条胜出"换成"第一条胜出"，同样静默，所以这里必须是抛错而不是忽略。
    [Fact]
    public void A_second_context_is_rejected_instead_of_silently_ignored()
    {
        var services = new ServiceCollection();
        services.AddPermissionAuthorizationEfCore<FirstDbContext>();

        var exception = Record.Exception(() => services.AddPermissionAuthorizationEfCore<SecondDbContext>());

        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("single authoritative store", exception!.Message);
    }

    // 宿主先注册了自己的实现：同样按冲突处理，不能被框架的默认实现悄悄挤掉或忽略。
    [Fact]
    public void A_host_registered_implementation_is_rejected_as_a_conflict()
    {
        var services = new ServiceCollection();
        // 工厂描述符的 ImplementationType 为 null，代表"宿主自己接了个实现"：同样构成冲突，
        // 不能被框架的默认实现悄悄挤掉或忽略。
        services.AddTransient<IPermissionGrantStore>(_ => throw new NotSupportedException());

        var exception = Record.Exception(() => services.AddPermissionAuthorizationEfCore<FirstDbContext>());

        Assert.IsType<InvalidOperationException>(exception);
    }

    // Manager 是业务编排，不是存储：宿主在组合根先注册自己的实现，框架不该覆盖，也不该
    // 追加第二条。这正是 TryAdd 表达的标准 DI 语义——若也套用"唯一权威"断言，
    // 合法的替换会被当成冲突拒掉。
    [Fact]
    public void A_host_registered_manager_is_kept_and_not_duplicated()
    {
        var services = new ServiceCollection();
        // 工厂注册即可代表"宿主自己接的实现"，不必为验证"没被覆盖"手写整个接口。
        services.AddTransient<IPermissionGrantManager>(_ => throw new NotSupportedException());

        services.AddPermissionAuthorizationEfCore<FirstDbContext>();

        var descriptor = services.AssertSingle<IPermissionGrantManager>(ServiceLifetime.Transient);
        Assert.NotNull(descriptor.ImplementationFactory);
    }

    // 宿主提前把同一个实现登记成错误的生命周期：必须拒绝——TryAdd 会保留宿主那条错的。
    [Fact]
    public void A_host_registration_with_the_wrong_lifetime_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPermissionGrantStore, EfCorePermissionGrantStore<FirstDbContext>>();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddPermissionAuthorizationEfCore<FirstDbContext>());

        Assert.Contains("Singleton", exception.Message);
    }

    [Fact]
    public void Repeated_registration_adds_nothing()
    {
        ServiceCollectionAssertions.AssertIdempotent(services =>
            services.AddPermissionAuthorizationEfCore<FirstDbContext>());
    }
}
