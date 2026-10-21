using Microsoft.AspNetCore.Authorization;
using Leistd.Authorization.Checking;

namespace Leistd.Authorization.AspNetCore.Permissions;

/// <summary>
/// 权限授权处理器：将 <see cref="PermissionRequirement"/> 委托给
/// <see cref="IPermissionChecker"/> 校验。
/// </summary>
public class PermissionAuthorizationHandler(IPermissionChecker permissionChecker)
    : AuthorizationHandler<PermissionRequirement>
{
    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        // 评估 context.User 而不是环境里的当前用户：经 IAuthorizationService 为别的主体判权时两者不同。
        // 多权限时任一满足即通过。
        var isGranted = requirement.PermissionNames.Count == 1
            ? await permissionChecker.IsGrantedAsync(context.User, requirement.PermissionNames[0])
            : (await permissionChecker.IsGrantedAsync(context.User, [.. requirement.PermissionNames])).AnyGranted;

        if (isGranted)
        {
            context.Succeed(requirement);
        }
    }
}

