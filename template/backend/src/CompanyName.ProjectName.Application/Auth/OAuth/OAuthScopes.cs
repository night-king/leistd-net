#if (OpenIddictServer)
#if (IncludeMultiTenancy)
using CompanyName.ProjectName.Application.TenantConnections.Constants;
#endif
using CompanyName.ProjectName.Application.Auth.Options;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CompanyName.ProjectName.Application.Auth.OAuth;

/// <summary>本授权服务器能签发的一个 scope。</summary>
/// <param name="Name">scope 名。</param>
/// <param name="DisplayName">展示名。</param>
/// <param name="Resources">授予该 scope 时访问令牌的受众（API 资源标识）。</param>
/// <param name="MachineOnly">只能发给 client_credentials 的机器客户端。</param>
public sealed record OAuthScope(string Name, string DisplayName, IReadOnlyList<string> Resources, bool MachineOnly);

/// <summary>本授权服务器的 scope 目录：服务端登记、scope 表、开放应用的权限校验与令牌受众都从这里取，不各自维护清单。</summary>
/// <remarks>
/// 本服务自己的 API 以 <see cref="OAuthResourceOptions.Resource"/> 同名登记为 scope；下游 API 由 <see cref="OAuthResourceOptions.ApiResources"/> 列出，
/// 各自登记为配置的 scope（默认同名）。访问令牌的受众由授予的 scope 推出：申请哪个 API 的 scope，令牌就只能调用那个 API。
/// </remarks>
public static class OAuthScopes
{
    /// <summary>全部 scope。</summary>
    public static IReadOnlyList<OAuthScope> All(OAuthResourceOptions options) =>
    [
        new(Scopes.OpenId, "OpenID", [], MachineOnly: false),
        new(Scopes.Profile, "Profile", [], MachineOnly: false),
        new(Scopes.Email, "Email", [], MachineOnly: false),
        new(Scopes.Roles, "Roles", [], MachineOnly: false),
        new(Scopes.OfflineAccess, "Offline access", [], MachineOnly: false),
        new(options.Resource, "API", [options.Resource], MachineOnly: false),
#if (IncludeMultiTenancy)
        new(TenantConnectionScopes.RuntimeRead, "Read tenant connection routing metadata", [options.Resource], MachineOnly: true),
        new(TenantConnectionScopes.MigrationRead, "Read tenant connection migration metadata", [options.Resource], MachineOnly: true),
#endif
        .. options.ApiResources.Select(api => new OAuthScope(api.ScopeName, api.ScopeName, [api.Name], MachineOnly: false)),
    ];

    /// <summary>交换发起方拥有的资源；本服务自己的资源默认由同名客户端拥有。</summary>
    public static IReadOnlyList<string> OwnedBy(OAuthResourceOptions options, string clientId) =>
        options.ApiResources.Where(api => api.Owner == clientId).Select(api => api.Name)
            .Concat(options.Resource == clientId ? [options.Resource] : [])
            .Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>授予这些 scope 时访问令牌的受众。</summary>
    public static IReadOnlyList<string> ResourcesOf(OAuthResourceOptions options, IEnumerable<string> scopes)
    {
        var granted = scopes.ToHashSet(StringComparer.Ordinal);
        return All(options)
            .Where(scope => granted.Contains(scope.Name))
            .SelectMany(scope => scope.Resources)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
#endif
