using Leistd.Authorization.AspNetCore;
using Leistd.Authorization.AspNetCore.Permissions;
using Leistd.TestBase.Assertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Authorization.Tests.AspNetCore;

/// <summary><c>AddPermissionAuthorization</c> 的注册面：策略提供器有意替换官方实现，授权处理器按实现去重。</summary>
/// <remarks>
/// 策略提供器只能有一条：两条并存时谁生效取决于注册顺序，权限名策略可能整体失效（官方实现不认识它们）。
/// 处理器重复时同一需求被判两次，单次检查的成本翻倍且日志成双。
/// </remarks>
public sealed class PermissionAuthorizationRegistrationTests
{
    [Fact]
    public void Registration_adds_the_policy_provider_and_the_handler()
    {
        var services = new ServiceCollection();

        services.AddPermissionAuthorization();

        services.AssertSingle<IAuthorizationPolicyProvider>(ServiceLifetime.Singleton);
        services.AssertImplementedBy<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        var handler = Assert.Single(services, d => d.ServiceType == typeof(IAuthorizationHandler));
        Assert.Equal(typeof(PermissionAuthorizationHandler), handler.ImplementationType);
        Assert.Equal(ServiceLifetime.Transient, handler.Lifetime);
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddPermissionAuthorization());

    // 官方 AddAuthorization 在前在后都只剩权限策略提供器一条
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Policy_provider_replaces_the_official_one_regardless_of_order(bool officialFirst)
    {
        var services = new ServiceCollection().AddLogging();
        if (officialFirst)
        {
            services.AddAuthorization();
        }

        services.AddPermissionAuthorization();

        if (!officialFirst)
        {
            services.AddAuthorization();
        }

        services.AssertImplementedBy<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
    }

    // 处理器是多实现扩展点：宿主自己的处理器与权限处理器并存
    [Fact]
    public void Host_authorization_handlers_coexist_with_the_permission_handler()
    {
        var services = new ServiceCollection();
        services.AddTransient<IAuthorizationHandler, PassThroughHandler>();

        services.AddPermissionAuthorization();
        services.AddPermissionAuthorization();

        var handlers = services.Where(d => d.ServiceType == typeof(IAuthorizationHandler)).Select(d => d.ImplementationType).ToList();
        Assert.Equal([typeof(PassThroughHandler), typeof(PermissionAuthorizationHandler)], handlers);
    }

    private sealed class PassThroughHandler : IAuthorizationHandler
    {
        public Task HandleAsync(AuthorizationHandlerContext context) => Task.CompletedTask;
    }
}
