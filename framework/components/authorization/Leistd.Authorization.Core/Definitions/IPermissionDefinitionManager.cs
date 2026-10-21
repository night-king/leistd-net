using Leistd.MultiTenancy.Tenancy;

namespace Leistd.Authorization.Definitions;

/// <summary>权限定义树的只读索引。</summary>
public interface IPermissionDefinitionManager
{
    /// <summary>按名称查找权限定义；不存在时返回 <see langword="null"/>。</summary>
    IPermissionDefinition? GetOrNull(string name);

    /// <summary>获取所有权限定义。</summary>
    IEnumerable<IPermissionDefinition> GetAll();

    /// <summary>获取全部权限组。</summary>
    IReadOnlyList<IPermissionGroupDefinition> GetGroups();

    /// <summary>判断权限是否已定义且自身与全部祖先都启用；定义不存在时返回 <see langword="false"/>。</summary>
    bool IsEffectivelyEnabled(string name);

    /// <summary>判断权限在给定侧别上是否可用：已定义、实际启用，且定义的侧别包含 <paramref name="side"/>。</summary>
    /// <remarks>权限检查器与管理用例共用此判据。</remarks>
    /// <param name="name">权限名。</param>
    /// <param name="side">当前侧别。</param>
    bool IsAvailableOn(string name, MultiTenancySides side)
        => IsEffectivelyEnabled(name) && GetOrNull(name)?.Side.HasFlag(side) == true;

    /// <summary>获取权限的全部祖先名称，由近到远；定义不存在时返回空集合。</summary>
    IReadOnlyList<string> GetAncestorNames(string name);

    /// <summary>获取权限的全部子孙名称；定义不存在时返回空集合。</summary>
    IReadOnlyList<string> GetDescendantNames(string name);
}
