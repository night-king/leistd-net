using System.Reflection;
using CompanyName.ProjectName.Application.Permissions.Provider;

namespace CompanyName.ProjectName.UnitTests.Application;

/// <summary>权限名的形状契约。</summary>
/// <remarks>
/// 这些常量同时是权限定义名、<c>[Authorize(Policy = ...)]</c> 的策略名和前端裁剪的契约，
/// 三处必须是同一组值。改错一个字符的表现是"端点永远 403"或"前端菜单永远不显示"，
/// 编译器不会提示，集成测试也只覆盖被用到的那几个。
/// 用反射一次覆盖全部——新增权限自动纳入，不必回来补用例。
/// </remarks>
public class PermissionContractTests
{
    private static IEnumerable<(string Path, string Value)> AllPermissionConstants(Type type, string prefix = "")
    {
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
        {
            if (field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            {
                yield return ($"{prefix}{type.Name}.{field.Name}", (string)field.GetRawConstantValue()!);
            }
        }

        foreach (var nested in type.GetNestedTypes(BindingFlags.Public))
        {
            foreach (var item in AllPermissionConstants(nested, $"{prefix}{type.Name}."))
            {
                yield return item;
            }
        }
    }

    /// <summary>可授予的权限名。</summary>
    /// <remarks>
    /// 排除两类非权限常量：分组标识符（模块那一级）与权限名前缀本身。
    /// </remarks>
    private static (string Path, string Value)[] Permissions() =>
        [.. AllPermissionConstants(typeof(PermissionConstant))
            .Where(x => !x.Path.Contains(".Groups.")
                        && x.Value != PermissionConstant.Prefix)];

    [Fact]
    public void Every_permission_starts_with_the_app_prefix()
    {
        Assert.All(Permissions(), p =>
            Assert.StartsWith(PermissionConstant.Prefix + ".", p.Value));
    }

    // 权限名要落库（授予记录存的是名字），重名等于两条不同的权限共用一条授予记录。
    [Fact]
    public void Permission_names_are_unique()
    {
        var duplicates = Permissions()
            .GroupBy(p => p.Value)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} ({string.Join(", ", g.Select(x => x.Path))})")
            .ToArray();

        Assert.True(duplicates.Length == 0, "Duplicate permission names: " + string.Join("; ", duplicates));
    }

    // 分组是"模块"这一级，不是权限。组名与任一权限同名会让同一个名字
    // 在组标题和组内各出现一次，读起来像重复项。
    [Fact]
    public void Group_identifiers_never_collide_with_permission_names()
    {
        var groups = AllPermissionConstants(typeof(PermissionConstant.Groups)).Select(g => g.Value).ToArray();

        Assert.NotEmpty(groups);
        Assert.All(groups, g => Assert.DoesNotContain(Permissions(), p => p.Value == g));
    }

    // 动作权限一律是"资源.动作"两段，且资源那一段本身也是一个权限（可授予的默认项）。
    [Fact]
    public void Action_permissions_extend_an_existing_resource_permission()
    {
        var names = Permissions().Select(p => p.Value).ToHashSet(StringComparer.Ordinal);

        foreach (var (_, value) in Permissions())
        {
            var lastDot = value.LastIndexOf('.');
            var parent = value[..lastDot];

            if (parent == PermissionConstant.Prefix)
            {
                continue;   // 资源级权限本身，没有更上层
            }

            Assert.Contains(parent, names);
        }
    }
}
