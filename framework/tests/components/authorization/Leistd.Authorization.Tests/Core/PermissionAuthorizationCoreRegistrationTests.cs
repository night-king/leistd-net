using Leistd.Authorization.Checking;
using Leistd.Authorization.Definitions;
using Leistd.Authorization.Grants;
using Leistd.Authorization.Management;
using Leistd.Authorization.Options;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Authorization.Tests.Core;

/// <summary><c>AddPermissionAuthorizationCore</c> 的注册面：生命周期、幂等与宿主替换。</summary>
public sealed class PermissionAuthorizationCoreRegistrationTests
{
    // 检查器是 Scoped：一次请求内的多次检查共享主体与授予快照；改成 Singleton 会把一个人的快照带给下一个请求
    [Fact]
    public void Registration_uses_the_documented_lifetimes()
    {
        var services = new ServiceCollection();

        services.AddPermissionAuthorizationCore();

        services.AssertSingle<IPermissionDefinitionManager>(ServiceLifetime.Singleton);
        services.AssertImplementedBy<IPermissionDefinitionManager, PermissionDefinitionManager>();
        services.AssertSingle<IPermissionChecker>(ServiceLifetime.Scoped);
        services.AssertImplementedBy<IPermissionChecker, DefaultPermissionChecker>();
        services.AssertSingle<IPermissionManagementService>(ServiceLifetime.Transient);
        services.AssertSingle<IPermissionGrantSeeder>(ServiceLifetime.Transient);
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddPermissionAuthorizationCore());

    // 组合根拆分时各处的展示约定按调用顺序叠加，后者覆盖前者
    [Fact]
    public void Repeated_registration_applies_each_configuration_in_order()
    {
        using var provider = new ServiceCollection()
            .AddPermissionAuthorizationCore(options => options.LocalizationResource = typeof(string))
            .AddPermissionAuthorizationCore(options => options.LocalizationResource = typeof(int))
            .BuildServiceProvider();

        Assert.Equal(typeof(int), provider.GetRequiredService<IOptions<PermissionManagementOptions>>().Value.LocalizationResource);
    }

    // 检查器是替换口：宿主先注册的实现不能被组件默认值盖掉
    [Fact]
    public void Host_registered_permission_checker_is_kept()
    {
        var services = new ServiceCollection();
        services.AddScoped<IPermissionChecker>(_ => throw new NotSupportedException());

        services.AddPermissionAuthorizationCore();

        Assert.NotNull(services.AssertSingle<IPermissionChecker>(ServiceLifetime.Scoped).ImplementationFactory);
    }
}
