using Leistd.TestBase.Doubles;
using System.Security.Claims;
using Xunit;
using Leistd.Authorization.Definitions;
using Leistd.Authorization.Grants;
using Leistd.Authorization.Subjects;
using Leistd.Authorization.Checking;
using Leistd.Authorization.Constants;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Tenancy;
using Leistd.Authorization.Tests.TestDoubles;

namespace Leistd.Authorization.Tests.Core;

/// <summary>权限多租户侧别：侧别是先于授予与超管旁路的硬边界。</summary>
public class PermissionSideTests
{
    private const string HostOnly = "App.Tenants";
    private const string HostOnlyChild = "App.Tenants.Create";
    private const string TenantOnly = "App.Workbench";
    private const string BothSides = "App.Orders";

    /// <summary>每条权限显式声明侧别；子权限不声明时继承父权限。</summary>
    private sealed class SidedDefinitionProvider : IPermissionDefinitionProvider
    {
        public void Define(IPermissionDefinitionContext context)
        {
            var system = context.GetOrAddGroup("Group.System", "系统");
            var tenants = system.AddPermission(HostOnly, MultiTenancySides.Host, "租户管理");
            tenants.AddChild(HostOnlyChild, "创建租户");

            var app = context.GetOrAddGroup("Group.App", "应用");
            app.AddPermission(BothSides, MultiTenancySides.Both, "订单管理");
            app.AddPermission(TenantOnly, MultiTenancySides.Tenant, "租户工作台");
        }
    }

    private sealed class FixedSubjectProvider(PermissionSubject? subject) : IPermissionSubjectProvider
    {
        public Task<PermissionSubject?> GetCurrentSubjectAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(subject);

        public Task<PermissionSubject?> GetSubjectAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
            => Task.FromResult(subject);
    }

    private sealed class AllGrantedStore : IPermissionGrantStore
    {
        private static readonly string[] All = [HostOnly, HostOnlyChild, TenantOnly, BothSides];

        public Task<PermissionGrantSet> GetGrantsAsync(string providerName, string providerKey, CancellationToken cancellationToken = default)
            => Task.FromResult(new PermissionGrantSet(providerName, providerKey, All, 1));

        public Task<IReadOnlyList<PermissionGrantSet>> GetGrantsAsync(string providerName, IReadOnlyCollection<string> providerKeys, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PermissionGrantSet>>(
                providerKeys.Select(k => new PermissionGrantSet(providerName, k, All, 1)).ToList());

        public Task<SubjectPermissionGrants> GetGrantsForSubjectAsync(string userId, IReadOnlyCollection<string> roleIds, CancellationToken cancellationToken = default)
            => Task.FromResult(new SubjectPermissionGrants(
                new PermissionGrantSet(PermissionGrantProviderNames.User, userId, All, 1),
                []));
    }

    private static DefaultPermissionChecker CreateChecker(Guid? tenantId, bool isSuperAdmin = false, bool tenantServiceRegistered = true)
        => new(
            new FixedSubjectProvider(new PermissionSubject("u1", [], isSuperAdmin)),
            TestPermissionDefinitions.CreateManager(new SidedDefinitionProvider()),
            new AllGrantedStore(),
            tenantServiceRegistered ? new FakeCurrentTenant(tenantId) : null);

    [Fact]
    public async Task Host_permission_is_denied_in_tenant_context_even_with_grant_and_super_admin()
    {
        var checker = CreateChecker(Guid.NewGuid(), isSuperAdmin: true);

        // 侧别是硬边界：授予了、甚至超管，都不放行宿主侧权限
        Assert.False(await checker.IsGrantedAsync(HostOnly));
        Assert.False(await checker.IsGrantedAsync(HostOnlyChild));
    }

    [Fact]
    public async Task Tenant_permission_is_denied_in_host_context()
    {
        var checker = CreateChecker(tenantId: null);

        Assert.False(await checker.IsGrantedAsync(TenantOnly));
        Assert.True(await checker.IsGrantedAsync(HostOnly));
    }

    [Fact]
    public async Task Both_side_permission_works_in_both_contexts()
    {
        Assert.True(await CreateChecker(tenantId: null).IsGrantedAsync(BothSides));
        Assert.True(await CreateChecker(Guid.NewGuid()).IsGrantedAsync(BothSides));
    }

    [Fact]
    public async Task Missing_current_tenant_service_means_host_side()
    {
        // 非多租户宿主（未注册 ICurrentTenant）：视为宿主侧，仅租户侧专属权限被拒
        var checker = CreateChecker(tenantId: null, tenantServiceRegistered: false);

        Assert.True(await checker.IsGrantedAsync(HostOnly));
        Assert.True(await checker.IsGrantedAsync(BothSides));
        Assert.False(await checker.IsGrantedAsync(TenantOnly));
    }

    [Fact]
    public async Task Batch_check_applies_the_same_side_boundary()
    {
        var result = await CreateChecker(Guid.NewGuid())
            .IsGrantedAsync([HostOnly, TenantOnly, BothSides]);

        Assert.False(result.Results[HostOnly]);
        Assert.True(result.Results[TenantOnly]);
        Assert.True(result.Results[BothSides]);
    }

    [Fact]
    public void Side_is_declared_per_permission_and_inherited_by_children()
    {
        var manager = TestPermissionDefinitions.CreateManager(new SidedDefinitionProvider());

        Assert.Equal(MultiTenancySides.Host, manager.GetOrNull(HostOnly)!.Side);
        Assert.Equal(MultiTenancySides.Host, manager.GetOrNull(HostOnlyChild)!.Side);
        Assert.Equal(MultiTenancySides.Both, manager.GetOrNull(BothSides)!.Side);
        Assert.Equal(MultiTenancySides.Tenant, manager.GetOrNull(TenantOnly)!.Side);
    }
}
