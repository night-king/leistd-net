using Microsoft.AspNetCore.Authorization;
using Leistd.Authorization.Checking;

namespace Leistd.Authorization.AspNetCore.Permissions;

/// <summary>权限授权需求；持有多个权限名时任一满足即通过。</summary>
/// <remarks>
/// 由 <see cref="PermissionPolicyProvider"/> 动态构建，由 <see cref="PermissionAuthorizationHandler"/> 经 <see cref="IPermissionChecker"/> 校验。
/// </remarks>
public class PermissionRequirement : IAuthorizationRequirement
{
    /// <summary>用单个权限构造。</summary>
    public PermissionRequirement(string permissionName)
        : this([permissionName])
    {
    }

    /// <summary>用一组权限构造，任一满足即通过。</summary>
    public PermissionRequirement(IReadOnlyList<string> permissionNames)
    {
        ArgumentNullException.ThrowIfNull(permissionNames);

        PermissionNames = permissionNames;
    }

    /// <summary>所需权限名集合，任一满足即通过。</summary>
    public IReadOnlyList<string> PermissionNames { get; }
}
