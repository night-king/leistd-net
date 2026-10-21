using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Leistd.Authorization.Constants;
using Leistd.Authorization.Definitions;

namespace Leistd.Authorization.AspNetCore.Permissions;

/// <summary>权限策略提供器：把权限名作为授权策略名。</summary>
/// <remarks>
/// policy 名命中已定义权限时动态构建携带 <see cref="PermissionRequirement"/> 的策略，否则回退到默认策略提供器。
/// 策略名可用 <c>|</c> 连接多个权限名表示"任一满足"；
/// 仅当每一段都是已定义权限时才按权限策略处理，避免误判恰好含 <c>|</c> 的自定义策略名。
/// </remarks>
public class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallbackPolicyProvider;
    private readonly IPermissionDefinitionManager _permissionDefinitionManager;

    /// <summary>创建策略提供方。默认与回落策略转发给 ASP.NET Core 的内置提供方。</summary>
    public PermissionPolicyProvider(
        IOptions<AuthorizationOptions> options,
        IPermissionDefinitionManager permissionDefinitionManager)
    {
        _fallbackPolicyProvider = new DefaultAuthorizationPolicyProvider(options);
        _permissionDefinitionManager = permissionDefinitionManager;
    }

    /// <inheritdoc />
    public async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        // 显式注册的同名策略优先（宿主可能在权限之外再要求 MFA 或特定 Claim）
        var registered = await _fallbackPolicyProvider.GetPolicyAsync(policyName);
        if (registered != null)
            return registered;

        var names = policyName.Split(PermissionPolicyNames.AnyOfSeparator, StringSplitOptions.TrimEntries);

        // 多段时要求每一段都已定义：只要有一段不是权限名，就不按权限策略处理。
        if (!Array.TrueForAll(names, name => _permissionDefinitionManager.GetOrNull(name) != null))
            return null;

        return new AuthorizationPolicyBuilder()
            .AddRequirements(new PermissionRequirement(names))
            .Build();
    }

    /// <inheritdoc />
    public Task<AuthorizationPolicy> GetDefaultPolicyAsync()
        => _fallbackPolicyProvider.GetDefaultPolicyAsync();

    /// <inheritdoc />
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync()
        => _fallbackPolicyProvider.GetFallbackPolicyAsync();
}

