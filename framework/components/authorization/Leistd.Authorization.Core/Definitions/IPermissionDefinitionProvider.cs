using Leistd.MultiTenancy.Tenancy;

namespace Leistd.Authorization.Definitions;

/// <summary>向定义上下文登记权限定义。</summary>
/// <example>
/// <code>
/// public class OrderPermissionDefinitionProvider : IPermissionDefinitionProvider
/// {
///     public void Define(IPermissionDefinitionContext context)
///     {
///         var group = context.GetOrAddGroup("Orders");
///         var orders = group.AddPermission("Orders.Read", MultiTenancySides.Both);
///         orders.AddChild("Orders.Update");   // 授予子权限时祖先自动补齐
///     }
/// }
/// </code>
/// </example>
public interface IPermissionDefinitionProvider
{
    /// <summary>定义权限。</summary>
    /// <param name="context">权限定义上下文。</param>
    void Define(IPermissionDefinitionContext context);
}

/// <summary>权限定义的登记与查询上下文。</summary>
/// <remarks>每个权限必须归属于一个权限组；权限名全局唯一，重复登记在启动期抛出。</remarks>
public interface IPermissionDefinitionContext
{
    /// <summary>获取或创建权限组。组不带侧别，侧别由每个权限在 <c>AddPermission</c> 处声明。</summary>
    /// <param name="name">组名称。</param>
    /// <param name="displayName">默认显示名，未启用本地化或缺词条时直接展示。</param>
    IPermissionGroupDefinition GetOrAddGroup(string name, string? displayName = null);

    /// <summary>获取权限定义；不存在时返回 <see langword="null"/>。</summary>
    /// <param name="name">权限名称。</param>
    IPermissionDefinition? GetPermissionOrNull(string name);
}

/// <summary>权限组定义。</summary>
public interface IPermissionGroupDefinition
{
    /// <summary>组名称。</summary>
    string Name { get; }

    /// <summary>默认显示名，未启用本地化或缺词条时直接展示；词条键为 <c>PermissionGroup:{组名}</c>。</summary>
    string? DisplayName { get; set; }

    /// <summary>组内的顶层权限，按声明顺序排列。</summary>
    IReadOnlyList<IPermissionDefinition> Permissions { get; }

    /// <summary>向组添加权限。</summary>
    /// <param name="name">权限名称。</param>
    /// <param name="side">多租户侧别，必填：按这条权限背后的数据是否带租户维度选择；宿主全局资源误设为 <c>Both</c> 即跨租户越权。</param>
    /// <param name="displayName">默认显示名，未启用本地化或缺词条时直接展示。</param>
    IPermissionDefinition AddPermission(string name, MultiTenancySides side, string? displayName = null);

    /// <summary>获取组内的权限定义；不存在或不属于本组时返回 <see langword="null"/>。</summary>
    /// <param name="name">权限名称。</param>
    IPermissionDefinition? GetPermissionOrNull(string name);
}

/// <summary>权限定义。</summary>
public interface IPermissionDefinition
{
    /// <summary>权限名称，全局唯一。</summary>
    string Name { get; }

    /// <summary>默认显示名，未启用本地化或缺词条时直接展示；词条键为 <c>Permission:{权限名}</c>。</summary>
    string? DisplayName { get; set; }

    /// <summary>父权限；顶层为 <see langword="null"/>。</summary>
    IPermissionDefinition? Parent { get; }

    /// <summary>子权限。</summary>
    IReadOnlyList<IPermissionDefinition> Children { get; }

    /// <summary>是否启用；祖先停用时本权限同样不可用。</summary>
    bool IsEnabled { get; set; }

    /// <summary>权限适用的多租户侧别。</summary>
    MultiTenancySides Side { get; }

    /// <summary>添加子权限。</summary>
    /// <param name="name">权限名称。</param>
    /// <param name="displayName">默认显示名，未启用本地化或缺词条时直接展示。</param>
    /// <param name="side">多租户侧别；不指定时继承父权限的侧别。</param>
    IPermissionDefinition AddChild(string name, string? displayName = null, MultiTenancySides? side = null);
}
