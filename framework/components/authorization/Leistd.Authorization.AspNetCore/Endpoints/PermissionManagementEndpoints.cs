using Leistd.Authorization.Management;
using Leistd.Authorization.Constants;
using Leistd.Authorization.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Leistd.Authorization.AspNetCore.Endpoints;

/// <summary>权限管理的 HTTP 端点。</summary>
public static class PermissionManagementEndpoints
{
    /// <summary>端点名前缀，宿主按名字给个别端点追加约定时使用。</summary>
    public const string NamePrefix = "Leistd.Authorization.";

    /// <summary>当前用户有效权限的端点名。</summary>
    public const string GetCurrentName = NamePrefix + "GetCurrent";

    /// <summary>权限定义树的端点名。</summary>
    public const string GetDefinitionsName = NamePrefix + "GetDefinitions";

    /// <summary>读取某类主体授予的端点名，如 <c>Leistd.Authorization.GetRoleGrants</c>。</summary>
    /// <param name="providerName">授予对象类型。</param>
    public static string GetGrantsName(string providerName) => $"{NamePrefix}Get{providerName}Grants";

    /// <summary>替换某类主体授予的端点名，如 <c>Leistd.Authorization.ReplaceRoleGrants</c>。</summary>
    /// <param name="providerName">授予对象类型。</param>
    public static string ReplaceGrantsName(string providerName) => $"{NamePrefix}Replace{providerName}Grants";

    private static readonly IReadOnlyDictionary<string, string> Segments = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [PermissionGrantProviderNames.Role] = "roles",
        [PermissionGrantProviderNames.User] = "users",
    };

    /// <summary>
    /// 映射权限管理：<c>GET /current</c>、<c>GET /definitions</c>，以及每个开放的主体类型的
    /// <c>GET /grants/{类型}/{providerKey}</c> 与 <c>PUT /grants/{类型}/{providerKey}</c>。
    /// </summary>
    /// <remarks>
    /// <para>全部要求已认证；<c>/current</c> 只要登录，其余按选项里的策略。
    /// 替换带期望版本，冲突返回 409；主体不存在 404；未定义或已停用的权限 400，都带错误码。</para>
    /// <para>前缀由宿主的路由组决定；返回的路由组可继续追加约定，个别端点按 <see cref="NamePrefix"/> 开头的端点名定位。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// app.MapGroup("/api/v1/permissions").MapPermissionManagement(options =&gt;
    /// {
    ///     // 权限树只为授予而读：能配置角色权限的人才需要它
    ///     options.DefinitionsPolicy = "App.Roles.ManagePermissions";
    ///     options.GrantPolicies[PermissionGrantProviderNames.Role] = "App.Roles.ManagePermissions";
    /// });
    /// </code>
    /// </example>
    /// <param name="endpoints">路由构建器（通常是宿主的路由组）。</param>
    /// <param name="configure">授权口径。</param>
    /// <returns>承载这组端点的路由组。</returns>
    public static RouteGroupBuilder MapPermissionManagement(
        this IEndpointRouteBuilder endpoints,
        Action<PermissionManagementEndpointOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new PermissionManagementEndpointOptions();
        configure(options);
        options.Validate([.. Segments.Keys]);

        var group = endpoints.MapGroup(string.Empty);

        group.MapGet("current", (IPermissionManagementService service, CancellationToken cancellationToken)
                => service.GetCurrentAsync(cancellationToken))
            .WithName(GetCurrentName)
            .RequireAuthorization(options.CurrentPolicy);

        group.MapGet("definitions", (IPermissionManagementService service, CancellationToken cancellationToken)
                => service.GetDefinitionsAsync(cancellationToken))
            .WithName(GetDefinitionsName)
            .RequireAuthorization(options.DefinitionsPolicy);

        foreach (var (providerName, policy) in options.GrantPolicies)
        {
            var route = $"grants/{Segments[providerName]}/{{providerKey}}";

            group.MapGet(route, (string providerKey, IPermissionManagementService service, CancellationToken cancellationToken)
                    => service.GetGrantsAsync(providerName, providerKey, cancellationToken))
                .WithName(GetGrantsName(providerName))
                .RequireAuthorization(policy);

            group.MapPut(route, (
                    string providerKey,
                    ReplacePermissionGrantsInputDto input,
                    IPermissionManagementService service,
                    CancellationToken cancellationToken)
                    => service.ReplaceGrantsAsync(providerName, providerKey, input, cancellationToken))
                .WithName(ReplaceGrantsName(providerName))
                .RequireAuthorization(policy);
        }

        return group;
    }
}
