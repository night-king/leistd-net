using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Leistd.AmbientContext;
using Leistd.MultiTenancy.AspNetCore.Options;
using Leistd.MultiTenancy.AspNetCore.Middlewares;
using Leistd.MultiTenancy.AspNetCore.AmbientContext;
using Leistd.MultiTenancy.AspNetCore.Resolution;
using Leistd.MultiTenancy.Resolution;

namespace Leistd.MultiTenancy.AspNetCore;

/// <summary>提供 ASP.NET Core 多租户注册和管道扩展。</summary>
public static class DependencyInjection
{
    /// <summary>注册多租户 Web 集成：绑定配置节，再应用代码里的配置（代码覆盖配置文件）。</summary>
    /// <remarks>
    /// 可重复调用：服务只注册一次，<paramref name="configure"/> 每次都叠加；
    /// 换用另一配置节时两个配置节都会绑定（后绑定的覆盖同名键），校验消息仍按首次调用的配置节给出键名。
    /// </remarks>
    /// <example>
    /// <code>
    /// // 宿主服务：持有租户注册表，解析后校验；选项全部来自 Leistd:MultiTenancy
    /// builder.Services.AddMultiTenancy();
    ///
    /// // 资源服务：只消费已验证令牌的 claim，无注册表
    /// builder.Services.AddMultiTenancy(options =&gt; options.ValidateResolvedTenant = false);
    ///
    /// // UseAuthentication() 之后、UseAuthorization() 之前
    /// app.UseMultiTenancy();
    /// </code>
    /// </example>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">代码里的配置，在配置节绑定之后应用。</param>
    /// <param name="configSectionPath">配置节路径，默认 <c>Leistd:MultiTenancy</c>。</param>
    public static IServiceCollection AddMultiTenancy(
        this IServiceCollection services,
        Action<MultiTenancyOptions>? configure = null,
        string configSectionPath = MultiTenancyOptions.SectionName)
    {
        var options = services.AddOptions<MultiTenancyOptions>().BindConfiguration(configSectionPath);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        return services.AddMultiTenancyInternal(configSectionPath);
    }

    private static IServiceCollection AddMultiTenancyInternal(this IServiceCollection services, string configSectionPath)
    {
        services.AddMultiTenancyCore();
        services.AddHttpContextAccessor();

        // 非 HTTP 入口的租户维度：Hub 调用、后台作业不经本组件的中间件，
        // 由环境上下文原语按主体的租户声明建立 ICurrentTenant。
        services.TryAddEnumerable(
            ServiceDescriptor.Transient<IAmbientContextContributor, TenantAmbientContextContributor>());

        // 域名是匿名请求的权威来源，格式错误必须在启动时失败。
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<MultiTenancyOptions>>(new MultiTenancyOptionsValidator(configSectionPath)));

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<MultiTenancyOptions>, TenantStoreRegistrationValidator>(
                provider => new TenantStoreRegistrationValidator(provider, configSectionPath)));

        services.AddOptions<MultiTenancyOptions>().ValidateOnStart();

        // 仅为空链装配默认贡献者，保留宿主自定义顺序。
        services.AddOptions<TenantResolveOptions>()
            .Configure(options =>
            {
                if (options.Contributors.Count > 0)
                {
                    return;
                }

                options.Contributors.Add(new CurrentPrincipalTenantResolveContributor());

                // 已配置域名时优先于外部可控的请求头和查询串。
                options.Contributors.Add(new DomainTenantResolveContributor());
                options.Contributors.Add(new HeaderTenantResolveContributor());
                options.Contributors.Add(new QueryStringTenantResolveContributor());
            });

        // 关闭注册表校验时在最终配置阶段强制只信主体 claim。
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPostConfigureOptions<TenantResolveOptions>,
                PrincipalOnlyTenantResolveEnforcer>());

        return services;
    }

    /// <summary>在认证和受信上下文恢复之后、授权之前挂载多租户中间件。</summary>
    /// <remarks>
    /// <c>ValidateResolvedTenant</c> 为 <see langword="true"/> 时校验注册表；为
    /// <see langword="false"/> 时只信已认证主体的租户声明。
    /// </remarks>
    public static IApplicationBuilder UseMultiTenancy(this IApplicationBuilder app)
    {
        return app.UseMiddleware<MultiTenancyMiddleware>();
    }

    /// <summary>
    /// 挂载租户会话自恢复：已认证的租户会话命中"租户不存在或已停用"时注销会话，页面导航重定向回原地址、
    /// 接口请求返回 401，并带上恢复标记头。
    /// </summary>
    /// <remarks>
    /// 置于 <c>UseAuthentication()</c> 之后、<see cref="UseMultiTenancy"/> 之前：它要接住多租户中间件抛出的租户异常。
    /// 不挂的话，被停用租户的用户连登录页与注销端点都访问不了，只能手动清 Cookie。
    /// </remarks>
    /// <example>
    /// <code>
    /// app.UseAuthentication();
    /// app.UseTenantSessionRecovery(options =&gt; options.SignOutScheme = CookieAuthenticationDefaults.AuthenticationScheme);
    /// app.UseMultiTenancy();
    /// </code>
    /// </example>
    /// <param name="app">应用管道。</param>
    /// <param name="configure">要注销的认证方案与标记头。</param>
    public static IApplicationBuilder UseTenantSessionRecovery(
        this IApplicationBuilder app,
        Action<TenantSessionRecoveryOptions>? configure = null)
    {
        var options = new TenantSessionRecoveryOptions();
        configure?.Invoke(options);
        options.Validate();
        return app.UseMiddleware<TenantSessionRecoveryMiddleware>(options);
    }
}
