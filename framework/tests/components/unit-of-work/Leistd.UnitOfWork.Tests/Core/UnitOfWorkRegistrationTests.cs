using Leistd.EventBus.Abstractions;
using Leistd.TestBase.Assertions;
using Leistd.UnitOfWork.Interceptors;
using Leistd.UnitOfWork.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.UnitOfWork.Tests.Core;

/// <summary><c>AddUnitOfWork</c> 的注册面：生命周期、幂等与宿主替换。</summary>
/// <remarks>
/// 环境工作单元与管理器是单例（状态在 AsyncLocal 里）；工作单元本身是 Transient，每次开始都是新的。
/// 管理器被改成 Scoped 时，后台作业在根作用域里拿不到它；工作单元被改成单例时，两个请求会共用一个事务。
/// </remarks>
public sealed class UnitOfWorkRegistrationTests
{
    [Fact]
    public void Core_registration_uses_the_documented_lifetimes()
    {
        var services = new ServiceCollection();

        services.AddUnitOfWork();

        services.AssertSingle<IAmbientUnitOfWork>(ServiceLifetime.Singleton);
        services.AssertSingle<IUnitOfWorkManager>(ServiceLifetime.Singleton);
        services.AssertSingle<IUnitOfWork>(ServiceLifetime.Transient);
        services.AssertSingle<UnitOfWorkInterceptor>(ServiceLifetime.Transient);
        services.AssertSingle<UnitOfWorkEventHandlerInterceptor>(ServiceLifetime.Transient);
        services.AssertSingle<ILocalEventDeferrer>(ServiceLifetime.Singleton);
    }

    [Fact]
    public void Core_registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddUnitOfWork());

    // 默认选项只有一份，校验消息只能按一个配置节报键名
    [Fact]
    public void A_second_registration_with_another_section_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddUnitOfWork();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddUnitOfWork(configSectionPath: "Ops:Transactions"));

        Assert.Contains("'Ops:Transactions'", exception.Message, StringComparison.Ordinal);
        services.AssertSingle<IValidateOptions<UnitOfWorkOptions>>(ServiceLifetime.Singleton);
    }

    // 管理器是替换口：宿主先注册的实现不被组件默认值盖掉
    [Fact]
    public void Host_registered_unit_of_work_manager_is_kept()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IUnitOfWorkManager>(_ => throw new NotSupportedException());

        services.AddUnitOfWork();

        Assert.NotNull(services.AssertSingle<IUnitOfWorkManager>(ServiceLifetime.Singleton).ImplementationFactory);
    }
}
