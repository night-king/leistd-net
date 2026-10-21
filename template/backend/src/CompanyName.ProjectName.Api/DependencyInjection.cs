// 授权结果处理器在所有服务形态下都存在（被拒的写端点要留痕），因此本 using 无条件
using CompanyName.ProjectName.Api.Auth.Authorization;
#if (!IncludeMultiTenancy)
using CompanyName.ProjectName.Api.Middlewares;
#endif
#if (LocalIdentity || ResourceBrowserSession)
using CompanyName.ProjectName.Application.Shared;
#endif
using Leistd.Security.Claims;
#if (OpenIddictServer && IncludeMultiTenancy)
using CompanyName.ProjectName.Application.TenantConnections.Constants;
#endif
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
#if (OpenIddictServer && IncludeMultiTenancy)
using OpenIddict.Abstractions;
#endif
using System.Security.Claims;
#if ((OpenIddictServer && IncludeMultiTenancy) || (RemoteTokenAuth && !ResourceBrowserSession))
using OpenIddict.Validation.AspNetCore;
#endif

namespace CompanyName.ProjectName.Api;

/// <summary>Api 层依赖注入配置。</summary>
/// <remarks>
/// 授权策略属于 Api 层职责（HTTP 认证方案与 Requirement），因此收在本文件而不是 <c>Program.cs</c>：
/// 与其余三层各自的 <c>DependencyInjection.cs</c> 同构，组合根只负责调用。
/// </remarks>
public static class DependencyInjection
{
    /// <summary>注册 Api 层授权：默认策略、相关 Handler 与内部控制面策略。</summary>
    /// <remarks>
    /// 各形态都在这里定案，组合根只有一行调用：本地身份形态的主体来自 Bearer 或会话 Cookie
    /// 并要求账号可用；资源服务形态来自 Bearer（带浏览器会话时还有服务端 Cookie），签发方的账号状态由签发方负责，
    /// 本服务自己的成员启停由 <c>LocalMemberAccessRequirement</c> 判定。
    /// </remarks>
    public static IServiceCollection AddApiAuthorization(this IServiceCollection services)
    {
        // ASP.NET Core 只认一个结果处理器，因此"账号失效改判 401"与"被拒写端点留痕"
        // 收在同一个类里，有意替换官方默认处理器（Replace 与 AddAuthorization 的先后无关）。
        // **无条件注册**：资源服务形态没有本地账号，但一样有带策略的写端点，
        // 放进 LocalIdentity 守卫会让那半边静默没有授权阶段的审计。
        services.Replace(ServiceDescriptor.Singleton<IAuthorizationMiddlewareResultHandler, ApiAuthorizationResultHandler>());

        services.AddAuthorization();
#if (RemoteTokenAuth)
        // 作用域服务：快照只活在一个请求里；Hub 每次方法调用是新作用域，取不到快照时按主键读一次
        // 重复调用不得重复登记：授权会枚举全部处理器，登记两次就判两次、Hub 调用里查两次库
        services.TryAddScoped<LocalMemberAccessSnapshot>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, LocalMemberAccessHandler>());
#endif
        // 默认策略要按宿主配置的主体标识 claim 判定自然人，因此经 Options 管道取 ClaimTypeOptions
        services.AddOptions<AuthorizationOptions>().Configure<IOptions<ClaimTypeOptions>>((options, claimTypeOptions) =>
        {
            var claimTypes = claimTypeOptions.Value;
#if (ResourceBrowserSession)
            // 资源服务按请求选择 Bearer 或 Cookie，默认策略要求自然人、且本服务没有停用这名成员；机器端点另设策略。
            // Hub 握手与每次方法调用也按这条策略判定（HubIdentityOptions.PolicyName 未设置即取默认策略）
            var currentUser = new AuthorizationPolicyBuilder(
                    AuthenticationSchemeNames.Smart)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => IsNaturalPerson(context.User, claimTypes))
                .AddRequirements(new LocalMemberAccessRequirement())
                .Build();
            options.DefaultPolicy = currentUser;
            // 组件的自用端点按名字要这条策略，见 ApiPolicies.CurrentUser
            options.AddPolicy(ApiPolicies.CurrentUser, currentUser);
#elif (RemoteTokenAuth)
            // 纯资源 API 只有 Bearer，默认策略要求自然人、且本服务没有停用这名成员；机器端点另设策略。
            // Hub 握手与每次方法调用也按这条策略判定（HubIdentityOptions.PolicyName 未设置即取默认策略）
            var currentUser = new AuthorizationPolicyBuilder(
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => IsNaturalPerson(context.User, claimTypes))
                .AddRequirements(new LocalMemberAccessRequirement())
                .Build();
            options.DefaultPolicy = currentUser;
            options.AddPolicy(ApiPolicies.CurrentUser, currentUser);
#else
            // 自然人主体可以来自两种方案：Bearer（签发形态）与会话 Cookie
            var humanSchemes = new[]
            {
#if (OpenIddictServer)
                AuthenticationSchemeNames.Smart
#else
                AuthenticationSchemeNames.SessionCookie
#endif
            };

            // 默认策略表达的是"一个自然人"，而不是"任何通过了认证的东西"：client_credentials 的令牌
            // （sub 形如 client:<client_id>，代表工作负载）不满足，答 403。不开角色时管理控制器只剩 [Authorize]，
            // 放行等于任何机器令牌都能列用户和 OAuth 客户端；面向工作负载的端点单独声明自己的策略。
            // 账号停用、删除后的撤权不在这里：会话与令牌在那一刻被撤销，认证阶段就不再通过
            var currentUser = new AuthorizationPolicyBuilder()
                .AddAuthenticationSchemes(humanSchemes)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => IsNaturalPerson(context.User, claimTypes))
                .Build();
            options.DefaultPolicy = currentUser;
            // 组件的自用端点（读设置、读自己的权限、通知中心）按名字要这条策略：
            // 组件不套宿主默认策略，要求什么必须写在映射处，见 ApiPolicies.CurrentUser
            options.AddPolicy(ApiPolicies.CurrentUser, currentUser);

#if (OpenIddictServer && IncludeMultiTenancy)
            AddMachineScopePolicy(options, claimTypes, TenantConnectionPolicies.RuntimeRead, TenantConnectionScopes.RuntimeRead);
            AddMachineScopePolicy(options, claimTypes, TenantConnectionPolicies.MigrationRead, TenantConnectionScopes.MigrationRead);
#endif
#endif
        });

        return services;
    }

    // 与 ICurrentUser.Id 同一口径：主体标识（按 ClaimTypeOptions.UserIds 读取）是用户 Id 才是自然人
    private static bool IsNaturalPerson(ClaimsPrincipal user, ClaimTypeOptions claimTypes) =>
        Guid.TryParse(claimTypes.FindUserId(user), out _)
#if (!IncludeMultiTenancy)
        && HostPrincipalMiddleware.IsHost(user, claimTypes)
#endif
        ;

#if (OpenIddictServer && IncludeMultiTenancy)
    /// <summary>注册一条只对<b>机器主体</b>开放的内部控制面策略。</summary>
    /// <remarks>
    /// 策略只接受 Bearer 认证的 <c>client:&lt;client_id&gt;</c> 机器主体，并要求对应 scope；
    /// Runtime 与 Migration scope 彼此独立。超管 claim 和会话 Cookie 均不能旁路该边界，
    /// 机器访问通过停用 Open Application 或撤销 scope 收回。
    /// </remarks>
    private static void AddMachineScopePolicy(
        AuthorizationOptions options,
        ClaimTypeOptions claimTypes,
        string policyName,
        string requiredScope)
    {
        options.AddPolicy(policyName, policy => policy
            .AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
                ClientSubject.IsMachine(claimTypes.FindUserId(context.User)) &&
                HasScope(context.User, requiredScope)));
    }

    private static bool HasScope(ClaimsPrincipal principal, string requiredScope) =>
        principal.Claims.Any(claim =>
            claim.Type == OpenIddictConstants.Claims.Scope &&
            claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(requiredScope, StringComparer.Ordinal));
#endif
}
