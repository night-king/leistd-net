#if (RemoteTokenAuth)
using CompanyName.ProjectName.Application.Permissions.Provider;
#endif
using Leistd.Authorization.Definitions;
using Microsoft.Extensions.DependencyInjection;
#if (IncludeLocalization)
using System.Globalization;
using CompanyName.ProjectName.Api.Localization;
using Microsoft.Extensions.Localization;
#endif

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>权限目录的后端约定：每个权限与分组都有可读的默认显示名，常规动作措辞统一，词条与默认文案一致。</summary>
/// <remarks>
/// 权限是授权定义，与前端菜单各自独立演进：菜单引用哪些权限、如何分组，由前端自己的测试约束，这里不读前端源码。
/// </remarks>
public sealed class PermissionCatalogContractTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    // 常规动作的统一措辞：英文是定义里的默认文案
    private static readonly Dictionary<string, string> CommonActions = new(StringComparer.Ordinal)
    {
        [".Create"] = "Create",
        [".Update"] = "Edit",
        [".Delete"] = "Delete",
    };

    private IReadOnlyList<IPermissionGroupDefinition> Groups()
        => factory.Services.GetRequiredService<IPermissionDefinitionManager>().GetGroups();

    private static IEnumerable<IPermissionDefinition> Flatten(IPermissionDefinition permission)
        => [permission, .. permission.Children.SelectMany(Flatten)];

    private IEnumerable<IPermissionDefinition> AllPermissions()
        => Groups().SelectMany(group => group.Permissions).SelectMany(Flatten);

    // 不启用本地化时对话框直接展示定义里的文案：它必须是给人看的，而不是技术名或词条键
    [Fact]
    public void Every_permission_and_group_has_a_readable_default_display_name()
    {
        // 分组名本身可以就是可读的英文单词（Audit、System），只要求有文案、不是词条键
        foreach (var group in Groups())
            AssertReadable(group.Name, group.DisplayName);
        // 权限名是 App.Users.Create 这类技术名，显示名不得等于它
        foreach (var permission in AllPermissions())
        {
            AssertReadable(permission.Name, permission.DisplayName);
            Assert.NotEqual(permission.Name, permission.DisplayName);
        }

        static void AssertReadable(string name, string? displayName)
        {
            Assert.False(string.IsNullOrWhiteSpace(displayName), $"{name} has no default display name");
            Assert.DoesNotContain(':', displayName!);
        }
    }

    // 资源服务形态下 App.Users.Update 只管启停（没有编辑端点，资料归签发方），
    // 显示名照实说，因此不套用 .Update 的共同措辞
    private static bool OptsOutOfSharedWording(string name)
#if (LocalIdentity)
        => false;
#else
        => name == PermissionConstant.Users.Update;
#endif

    [Fact]
    public void Common_actions_use_the_shared_wording()
    {
        foreach (var permission in AllPermissions().Where(p => !OptsOutOfSharedWording(p.Name)))
        {
            var action = CommonActions.Keys.FirstOrDefault(suffix => permission.Name.EndsWith(suffix, StringComparison.Ordinal));
            if (action is not null)
                Assert.Equal(CommonActions[action], permission.DisplayName);
        }
    }

#if (IncludeLocalization)

    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void Every_permission_and_group_is_translated(string culture)
    {
        var localizer = factory.Services.GetRequiredService<IStringLocalizerFactory>().Create(typeof(ApiResource));
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try
        {
            string Text(string key)
            {
                var localized = localizer[key];
                Assert.False(localized.ResourceNotFound, $"{culture} is missing localization entry {key}");
                return localized.Value;
            }

            foreach (var permission in AllPermissions())
            {
                var text = Text("Permission:" + permission.Name);
                // 英文资源与定义里的默认文案是同一句话的两份，必须一致，否则改了一处另一处静默盖掉它
                if (culture == "en")
                    Assert.Equal(permission.DisplayName, text);
            }

            foreach (var group in Groups())
            {
                var text = Text("PermissionGroup:" + group.Name);
                if (culture == "en")
                    Assert.Equal(group.DisplayName, text);
            }

            if (culture == "zh-CN")
            {
                // 缺中文词条时本地化回落到英文，所以"中文能查到"永远成立——删一条词条这个用例照样绿。
                // 判据必须是"中文取值不等于英文默认文案"：这样删任何一条都会红，而不只是下面那三个后缀。
                foreach (var permission in AllPermissions())
                {
                    var key = "Permission:" + permission.Name;
                    Assert.NotEqual(permission.DisplayName, Text(key));
                }

                foreach (var group in Groups())
                {
                    var key = "PermissionGroup:" + group.Name;
                    Assert.NotEqual(group.DisplayName, Text(key));
                }

                // 中文侧的共同措辞也要统一，与英文侧的 Common_actions_use_the_shared_wording 对应
                var chinese = new Dictionary<string, string> { [".Create"] = "新建", [".Update"] = "编辑", [".Delete"] = "删除" };
                foreach (var permission in AllPermissions().Where(p => !OptsOutOfSharedWording(p.Name)))
                {
                    var action = chinese.Keys.FirstOrDefault(suffix => permission.Name.EndsWith(suffix, StringComparison.Ordinal));
                    if (action is not null)
                        Assert.Equal(chinese[action], Text("Permission:" + permission.Name));
                }
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
#endif
}
