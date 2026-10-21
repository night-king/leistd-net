using Leistd.Authorization.Resource;
using Leistd.Authorization.Resource.Abstractions;
using Leistd.Authorization.Resource.AspNetCore;
using Leistd.Authorization.Resource.AspNetCore.Operations;
using Leistd.Authorization.Resource.Errors;
using Leistd.ExceptionHandling.Options;
using Leistd.Localization.Options;
using Leistd.TestBase.Assertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Authorization.Resource.Tests;

/// <summary><c>AddResourceAuthorization</c> 与 <c>AddResourceAuthorizationCore</c> 的注册面。</summary>
/// <remarks>
/// ACL 处理器重复登记时同一资源被判两遍；处理器按 TryAdd 去重又会把宿主的领域规则处理器挤掉——
/// 两种错都只在运行期以"越权"或"多一倍查询"显形。
/// </remarks>
public sealed class ResourceAuthorizationRegistrationTests
{
    [Fact]
    public void Registration_adds_the_entry_point_and_the_acl_handler()
    {
        var services = new ServiceCollection();

        services.AddResourceAuthorization();

        services.AssertSingle<IResourceAuthorizationService>(ServiceLifetime.Scoped);
        services.AssertImplementedBy<IResourceAuthorizationService, ResourceAuthorizationService>();
        var handler = Assert.Single(services, d => d.ImplementationType == typeof(ResourceGrantAuthorizationHandler));
        Assert.Equal(typeof(IAuthorizationHandler), handler.ServiceType);
        Assert.Equal(ServiceLifetime.Scoped, handler.Lifetime);
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddResourceAuthorization());

    // 处理器是多实现扩展点：宿主的领域规则处理器与 ACL 处理器并存，各一条
    [Fact]
    public void Host_rule_handlers_coexist_with_the_acl_handler()
    {
        var services = new ServiceCollection();
        services.AddScoped<IAuthorizationHandler, HostRuleHandler>();

        services.AddResourceAuthorization();
        services.AddResourceAuthorization();

        // 官方授权核心另有自己的处理器，这里只看宿主与本组件的两条
        var handlers = services.Where(d => d.ServiceType == typeof(IAuthorizationHandler)).Select(d => d.ImplementationType).ToList();
        Assert.Single(handlers, t => t == typeof(HostRuleHandler));
        Assert.Single(handlers, t => t == typeof(ResourceGrantAuthorizationHandler));
    }

    [Fact]
    public void Host_registered_entry_point_is_kept()
    {
        var services = new ServiceCollection();
        services.AddScoped<IResourceAuthorizationService>(_ => throw new NotSupportedException());

        services.AddResourceAuthorization();

        Assert.NotNull(services.AssertSingle<IResourceAuthorizationService>(ServiceLifetime.Scoped).ImplementationFactory);
    }

    // AspNetCore 与 EF 两个入口都会调 Core：重复登记不得让默认译文的程序集出现两次，默认状态仍然生效
    [Fact]
    public void Repeated_core_registration_keeps_one_resource_assembly_and_the_default_status()
    {
        using var provider = new ServiceCollection()
            .AddResourceAuthorizationCore()
            .AddResourceAuthorizationCore()
            .BuildServiceProvider();

        var assemblies = provider.GetRequiredService<IOptions<JsonLocalizationOptions>>().Value.ResourceAssemblies;
        Assert.Single(assemblies, a => a == typeof(ResourceAuthorizationErrorCodes).Assembly);
        Assert.Equal(
            StatusCodes.Status409Conflict,
            provider.GetRequiredService<IOptions<GlobalExceptionOptions>>().Value
                .CodeStatusMappings[ResourceAuthorizationErrorCodes.ConcurrencyConflict]);
    }

    private sealed class HostRuleHandler : IAuthorizationHandler
    {
        public Task HandleAsync(AuthorizationHandlerContext context) => Task.CompletedTask;
    }
}
