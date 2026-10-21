using Microsoft.Extensions.DependencyInjection;
#if (RemoteTokenAuth)
using Leistd.Authorization.Definitions;
using CompanyName.ProjectName.Api;
using CompanyName.ProjectName.Api.Auth.Authorization;
using Leistd.Security.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using System.Security.Claims;
#endif
#if (OpenIddictServer)
#if (IncludeMultiTenancy)
using CompanyName.ProjectName.Application.TenantConnections.Constants;
#endif
using Microsoft.Extensions.Options;
using OpenIddict.Server;
#if (IncludeMultiTenancy)
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions;
using Leistd.Security.Claims;
#endif

namespace CompanyName.ProjectName.IntegrationTests;

public sealed class AuthenticationModeTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    [Fact]
    public void Identity_access_tokens_expire_after_ten_minutes()
    {
        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>()
            .CurrentValue;

        Assert.Equal(TimeSpan.FromMinutes(10), options.AccessTokenLifetime);
    }

#if (IncludeMultiTenancy)
    [Fact]
    public async Task Identity_registers_runtime_and_migration_tenant_connection_scopes()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var scopeManager = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();

        Assert.NotNull(await scopeManager.FindByNameAsync("tenant-routing.read"));
        Assert.NotNull(await scopeManager.FindByNameAsync("tenant-migration.read"));
    }

    /// <summary>内部控制面策略的完整判定矩阵。</summary>
    /// <remarks>
    /// <para>这两条策略保护的端点直接返回租户连接配置（数据落在哪个库、解密后的连接串），
    /// 且接受<b>任意</b> <c>tenantId</c>——控制库是普通 <c>DbContext</c>、没有租户过滤器。
    /// 因此它们必须只对机器主体开放：任何自然人主体命中都是跨租户越权。</para>
    /// <para>因此判定里<b>不能</b>有"或者是超管"这一支：超管 claim 不经权限检查器、不受侧别边界
    /// 约束，只要有一个租户内的主体拿到它，就能读到别的租户的数据库连接串。这组用例把每一条路
    /// 都钉死，包括那条看起来很自然的旁路。</para>
    /// <para>认证方案层面的收窄（只列 Bearer、Cookie 到不了）无法在此断言——
    /// <c>IAuthorizationService.AuthorizeAsync</c> 不经过认证中间件；它由策略注册本身保证。</para>
    /// </remarks>
    [Theory]
    // 机器主体 + 正确 scope：唯一放行的组合
    [InlineData("client:svc", "tenant-routing.read", true)]
    // 机器主体 + 错误 scope
    [InlineData("client:svc", "tenant-migration.read", false)]
    // 机器主体 + 无 scope
    [InlineData("client:svc", null, false)]
    // 自然人主体（GUID sub）+ 正确 scope：用户令牌不得命中机器端点
    [InlineData("8f14e45f-ea6a-4c4b-9b2b-7c1f0a2d3e4f", "tenant-routing.read", false)]
    // 完全没有 sub
    [InlineData(null, "tenant-routing.read", false)]
    public async Task Runtime_policy_only_admits_machine_principals(
        string? subject,
        string? scope,
        bool expected)
    {
        var result = await AuthorizeAsync(TenantConnectionPolicies.RuntimeRead, subject, scope);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(TenantConnectionPolicies.RuntimeRead)]
    [InlineData(TenantConnectionPolicies.MigrationRead)]
    public async Task Super_admin_claim_does_not_admit_machine_endpoints(string policyName)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(OpenIddictConstants.Claims.Subject, "8f14e45f-ea6a-4c4b-9b2b-7c1f0a2d3e4f"),
                new Claim(CustomClaimTypes.IsSuperAdmin, "true")
            ],
            "test"));

        var result = await authorization.AuthorizeAsync(principal, resource: null, policyName);

        Assert.False(result.Succeeded);
    }

    /// <summary>两条 scope 彼此独立：Runtime 的 scope 读不到 Migration</summary>
    [Fact]
    public async Task Runtime_routing_scope_cannot_read_migration_metadata()
    {
        Assert.True(await AuthorizeAsync(
            TenantConnectionPolicies.RuntimeRead, "client:svc", "tenant-routing.read"));
        Assert.False(await AuthorizeAsync(
            TenantConnectionPolicies.MigrationRead, "client:svc", "tenant-routing.read"));
    }

    private async Task<bool> AuthorizeAsync(string policyName, string? subject, string? scope)
    {
        var claims = new List<Claim>();
        if (subject is not null)
        {
            claims.Add(new Claim(OpenIddictConstants.Claims.Subject, subject));
        }

        if (scope is not null)
        {
            claims.Add(new Claim(OpenIddictConstants.Claims.Scope, scope));
        }

        await using var serviceScope = factory.Services.CreateAsyncScope();
        var authorization = serviceScope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        var result = await authorization.AuthorizeAsync(principal, resource: null, policyName);
        return result.Succeeded;
    }
#endif

}
#endif
#if (RemoteTokenAuth)

namespace CompanyName.ProjectName.IntegrationTests;

public sealed class AuthenticationModeTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    [Theory]
    [InlineData("8f14e45f-ea6a-4c4b-9b2b-7c1f0a2d3e4f", true)]
    [InlineData("client:orders", false)]
    [InlineData("orders", false)]
    [InlineData(null, false)]
    public async Task Resource_default_and_current_user_policies_require_a_natural_person(string? subject, bool expected)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSecurity();
        services.AddApiAuthorization();
        // 这里只看自然人断言：成员状态的判定要查库，由 ResourceMemberAccessTests 在真实宿主上验证，此处判为满足
        services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(LocalMemberAccessHandler)));
        services.AddSingleton<IAuthorizationHandler, SatisfiedLocalMember>();
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var claims = subject is null ? Array.Empty<Claim>() : [new Claim("sub", subject)];
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "verified-token"));

        Assert.Equal(expected, (await authorization.AuthorizeAsync(user, null, options.DefaultPolicy)).Succeeded);
        Assert.Equal(expected, (await authorization.AuthorizeAsync(user, null, options.GetPolicy(ApiPolicies.CurrentUser)!)).Succeeded);
    }

    [Fact]
    public void Resource_default_and_current_user_policies_require_an_enabled_local_member()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSecurity();
        services.AddApiAuthorization();
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        // Hub 握手与方法调用复评取默认策略，组件自用端点取 CurrentUser：两条都要带成员要求
        Assert.Contains(options.DefaultPolicy.Requirements, requirement => requirement is LocalMemberAccessRequirement);
        Assert.Contains(options.GetPolicy(ApiPolicies.CurrentUser)!.Requirements, requirement => requirement is LocalMemberAccessRequirement);
    }

    private sealed class SatisfiedLocalMember : AuthorizationHandler<LocalMemberAccessRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, LocalMemberAccessRequirement requirement)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Resource_does_not_publish_identity_tenant_control_permissions()
    {
        var definitions = factory.Services.GetRequiredService<IPermissionDefinitionManager>();

        Assert.DoesNotContain(
            definitions.GetAll(),
            definition => definition.Name.EndsWith(".Tenants", StringComparison.Ordinal));
    }
}
#endif
