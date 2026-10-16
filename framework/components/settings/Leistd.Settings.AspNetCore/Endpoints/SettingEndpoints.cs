using Leistd.Settings.Definitions;
using Leistd.Settings.Management;
using Leistd.Settings.Dtos;
using Leistd.Settings.Options;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Leistd.Settings.AspNetCore.Endpoints;

/// <summary>设置页的 HTTP 端点。</summary>
public static class SettingEndpoints
{
    /// <summary>端点名前缀，宿主按名字给个别端点追加约定时使用。</summary>
    public const string NamePrefix = "Leistd.Settings.";

    /// <summary>读取端点名。</summary>
    public const string GetName = NamePrefix + "Get";

    /// <summary>写当前用户偏好的端点名。</summary>
    public const string SetForCurrentUserName = NamePrefix + "SetForCurrentUser";

    /// <summary>写当前租户设置的端点名。</summary>
    public const string SetForCurrentTenantName = NamePrefix + "SetForCurrentTenant";

    /// <summary>映射设置页端点：<c>GET /</c>、<c>PUT /current-user</c>、<c>PUT /current-tenant</c>。</summary>
    /// <remarks>
    /// <para>两个写入端点授权不同：改自己的偏好走 <see cref="SettingEndpointOptions.AccessPolicy"/>，
    /// 改租户值走 <see cref="SettingEndpointOptions.TenantWritePolicy"/>，分组登记了
    /// <see cref="SettingEndpointOptions.GroupPolicies"/> 时还要满足该分组的策略。读取按同一判据给出
    /// <see cref="SettingOutputDto.CanManage"/>，不能管理又不允许用户覆盖的设置不下发。</para>
    /// <para>写入成功返回 204；取值、层级与可见性错误是带码的 400 / 403 / 404。
    /// 前缀由宿主的路由组决定；返回的路由组可继续追加约定。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// app.MapGroup("/api/v1/settings").MapSettings(options =&gt;
    /// {
    ///     options.AccessPolicy = "App.CurrentUser";
    ///     options.TenantWritePolicy = "App.Settings";
    /// });
    /// </code>
    /// </example>
    /// <param name="endpoints">路由构建器（通常是宿主的路由组）。</param>
    /// <param name="configure">授权口径。</param>
    /// <returns>承载这组端点的路由组。</returns>
    public static RouteGroupBuilder MapSettings(
        this IEndpointRouteBuilder endpoints,
        Action<SettingEndpointOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SettingEndpointOptions();
        configure(options);
        options.Validate();

        var group = endpoints.MapGroup(string.Empty);

        group.MapGet(string.Empty, async (
                HttpContext httpContext,
                ISettingManagementService service,
                IAuthorizationPolicyProvider policies,
                IPolicyEvaluator evaluator,
                CancellationToken cancellationToken) =>
            {
                var settings = await service.GetAsync(cancellationToken);
                var access = new ManagementAccess(options, policies, evaluator, httpContext);
                var result = new List<SettingOutputDto>(settings.Count);
                foreach (var setting in settings)
                {
                    var canManage = (setting.AllowsTenantScope || setting.AllowsHostScope)
                                    && await access.CanManageAsync(setting.Group);
                    // 管理值只给管理者；允许用户覆盖的设置照常下发，个人设置页要用租户默认值作占位
                    if (canManage || setting.AllowsUserScope)
                    {
                        result.Add(setting with { CanManage = canManage });
                    }
                }

                return result;
            })
            .WithName(GetName)
            .RequireAuthorization(options.AccessPolicy);

        group.MapPut("current-user", async (SetSettingInputDto input, ISettingManagementService service, CancellationToken cancellationToken) =>
            {
                await service.SetForCurrentUserAsync(input, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName(SetForCurrentUserName)
            .RequireAuthorization(options.AccessPolicy);

        group.MapPut("current-tenant", async Task<IResult> (
                SetSettingInputDto input,
                HttpContext httpContext,
                ISettingManagementService service,
                ISettingDefinitionManager definitions,
                IAuthorizationPolicyProvider policies,
                IPolicyEvaluator evaluator,
                IOptions<SettingManagementOptions> managementOptions,
                CancellationToken cancellationToken) =>
            {
                // 未定义或不可见的名字交给用例按 404 处理，这里只判断登记了策略的分组
                var definition = definitions.GetOrNull(input.Name);
                if (definition is { IsVisibleToClients: true })
                {
                    var settingGroup = string.IsNullOrWhiteSpace(definition.Group)
                        ? managementOptions.Value.DefaultGroup
                        : definition.Group;
                    var denied = await new ManagementAccess(options, policies, evaluator, httpContext)
                        .DenyGroupAsync(settingGroup);
                    if (denied is not null)
                    {
                        return denied;
                    }
                }

                await service.SetForCurrentTenantAsync(input, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName(SetForCurrentTenantName)
            .RequireAuthorization(options.TenantWritePolicy);

        return group;
    }

    // 一次请求内按策略复用授权结果，不逐设置评估。走官方策略评估器：策略自带的认证方案照常生效、
    // 资源与端点授权一样是 HttpContext；评估某个策略的方案时会替换 HttpContext.User，评估完即还原
    private sealed class ManagementAccess(
        SettingEndpointOptions options,
        IAuthorizationPolicyProvider policies,
        IPolicyEvaluator evaluator,
        HttpContext httpContext)
    {
        private readonly Dictionary<string, (AuthorizationPolicy Policy, PolicyAuthorizationResult Result)> _results =
            new(StringComparer.Ordinal);

        // 读取用：TenantWritePolicy 与分组策略都满足
        public async Task<bool> CanManageAsync(string group)
            => (await EvaluateAsync(options.TenantWritePolicy)).Result.Succeeded
               && (!options.GroupPolicies.TryGetValue(group, out var policy) || (await EvaluateAsync(policy)).Result.Succeeded);

        // 写入用：TenantWritePolicy 已由端点约定保证，只补分组策略；不满足时按该策略的方案质询或拒绝
        public async Task<IResult?> DenyGroupAsync(string group)
        {
            if (!options.GroupPolicies.TryGetValue(group, out var name))
            {
                return null;
            }

            var (policy, result) = await EvaluateAsync(name);
            if (result.Succeeded)
            {
                return null;
            }

            return result.Challenged
                ? TypedResults.Challenge(authenticationSchemes: [.. policy.AuthenticationSchemes])
                : TypedResults.Forbid(authenticationSchemes: [.. policy.AuthenticationSchemes]);
        }

        private async Task<(AuthorizationPolicy Policy, PolicyAuthorizationResult Result)> EvaluateAsync(string name)
        {
            if (_results.TryGetValue(name, out var cached))
            {
                return cached;
            }

            var policy = await policies.GetPolicyAsync(name)
                         ?? throw new InvalidOperationException($"Authorization policy '{name}' is not registered.");
            // 只有策略自带认证方案时评估器才替换 HttpContext.User；评估完还原，否则后面的分组与端点看到的是别的方案的身份
            var user = httpContext.User;
            var authenticated = httpContext.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult;
            try
            {
                var authentication = await evaluator.AuthenticateAsync(policy, httpContext);
                var result = await evaluator.AuthorizeAsync(policy, authentication, httpContext, httpContext);
                _results[name] = (policy, result);
                return (policy, result);
            }
            finally
            {
                if (policy.AuthenticationSchemes.Count > 0)
                {
                    Restore(user, authenticated);
                }
            }
        }

        // 认证特性把主体与认证结果绑在一起：给 User 赋值会清掉结果，给结果赋值会带出它的主体。
        // 原先有结果且主体就是它的，还原结果；否则只还原主体
        private void Restore(ClaimsPrincipal user, AuthenticateResult? authenticated)
        {
            var feature = httpContext.Features.Get<IAuthenticateResultFeature>();
            if (feature is not null && authenticated is not null && ReferenceEquals(authenticated.Principal, user))
            {
                feature.AuthenticateResult = authenticated;
            }
            else
            {
                httpContext.User = user;
            }
        }
    }
}
