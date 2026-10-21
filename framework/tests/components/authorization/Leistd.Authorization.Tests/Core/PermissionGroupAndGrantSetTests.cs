using Leistd.MultiTenancy.Tenancy;
using Leistd.Authorization.Definitions;
using Leistd.Authorization.Grants;
using Leistd.Authorization.Constants;
using Leistd.Authorization.Tests.TestDoubles;
using Xunit;

namespace Leistd.Authorization.Tests.Core;

/// <summary>权限组的归属判定与授予集合的空值构造。</summary>
/// <remarks>
/// <c>IPermissionGroupDefinition.GetPermissionOrNull</c> 是权限树 UI 的按组查询入口，
/// 归属判定错了会把别的组的权限显示进来——那是一条越权配置路径。
/// </remarks>
public class PermissionGroupAndGrantSetTests
{
    private static IPermissionGroupDefinition Group() =>
        Assert.Single(TestPermissionDefinitions.CreateManager().GetGroups());

    [Fact]
    public void Group_resolves_its_own_top_level_permission()
    {
        var permission = Group().GetPermissionOrNull(TestPermissionDefinitionProvider.Orders);

        Assert.NotNull(permission);
        Assert.Equal(TestPermissionDefinitionProvider.Orders, permission.Name);
    }

    // 子孙权限也属于本组：归属判定要一路上溯到根，只比较直接成员会漏掉整棵子树。
    [Fact]
    public void Group_resolves_permissions_nested_below_its_own_roots()
    {
        var group = Group();

        Assert.NotNull(group.GetPermissionOrNull(TestPermissionDefinitionProvider.OrdersWrite));
        Assert.NotNull(group.GetPermissionOrNull(TestPermissionDefinitionProvider.OrdersWriteBatch));
    }

    [Fact]
    public void Group_returns_null_for_an_undefined_permission()
    {
        Assert.Null(Group().GetPermissionOrNull(TestPermissionDefinitionProvider.Undefined));
    }

    /// <summary>另一个组里的权限必须返回 null。</summary>
    /// <remarks>
    /// 归属判定若只查全局注册表，任何组都会"查得到"任何权限——
    /// 权限树按组渲染时就会把别的组的权限显示进来，那是一条越权配置路径。
    /// </remarks>
    [Fact]
    public void Group_returns_null_for_a_permission_owned_by_another_group()
    {
        var manager = TestPermissionDefinitions.CreateManager(
            new TestPermissionDefinitionProvider(), new OtherGroupProvider());

        var appGroup = manager.GetGroups().Single(g => g.Name == "App");

        Assert.NotNull(manager.GetOrNull(OtherGroupProvider.Thing));
        Assert.Null(appGroup.GetPermissionOrNull(OtherGroupProvider.Thing));
    }

    private sealed class OtherGroupProvider : IPermissionDefinitionProvider
    {
        public const string Thing = "Other.Thing";

        public void Define(IPermissionDefinitionContext context) =>
            context.GetOrAddGroup("Other", "另一个组").AddPermission(Thing, MultiTenancySides.Both, "别的东西");
    }

    // GetAll 拉平整棵树：权限配置界面按它渲染，少一层就是少一批可授予项。
    [Fact]
    public void Manager_get_all_flattens_every_level()
    {
        var all = TestPermissionDefinitions.CreateManager().GetAll().Select(p => p.Name).ToArray();

        Assert.Contains(TestPermissionDefinitionProvider.Orders, all);
        Assert.Contains(TestPermissionDefinitionProvider.OrdersWrite, all);
        Assert.Contains(TestPermissionDefinitionProvider.OrdersWriteBatch, all);
        Assert.DoesNotContain(TestPermissionDefinitionProvider.Undefined, all);
    }

    // 空授予集的版本必须是 0：调用方据此判断"这个主体从未被写过授予"，
    // 给成 -1 或 1 都会让客户端缓存失效判断出错。
    [Fact]
    public void Empty_grant_set_carries_the_subject_and_a_zero_version()
    {
        var empty = PermissionGrantSet.Empty(PermissionGrantProviderNames.User, "user-1");

        Assert.Equal(PermissionGrantProviderNames.User, empty.ProviderName);
        Assert.Equal("user-1", empty.ProviderKey);
        Assert.Empty(empty.PermissionNames);
        Assert.Equal(0, empty.Version);
    }
}
