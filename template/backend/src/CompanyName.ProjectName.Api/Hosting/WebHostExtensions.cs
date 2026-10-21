using System.Net;
#if (RemoteTokenAuth)
using CompanyName.ProjectName.Api.HealthChecks;
#endif
using CompanyName.ProjectName.Api.HostedServices.Initializer;
#if (IncludeLocalization)
using CompanyName.ProjectName.Api.Localization;
#endif
using Leistd.ExceptionHandling.AspNetCore;
#if (SpaFrontend)
using Leistd.Security.AspNetCore;
#endif
#if (LocalIdentity && IncludeMultiTenancy)
using Leistd.MultiTenancy.AspNetCore.Options;
#endif
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
#if (RemoteTokenAuth)
using Microsoft.Extensions.DependencyInjection.Extensions;
#endif
using Microsoft.Extensions.Diagnostics.HealthChecks;
// 与已过时的 Microsoft.AspNetCore.HttpOverrides.IPNetwork 同名，用别名指定 System.Net 的实现
using IPNetwork = System.Net.IPNetwork;

namespace CompanyName.ProjectName.Api.Hosting;

/// <summary>Web 宿主的注册入口：启动引导、健康检查、控制器与 JSON、OpenAPI、转发头与跨域。</summary>
public static class WebHostExtensions
{
    /// <summary>注册 Web 宿主自身的服务。</summary>
    public static IServiceCollection AddMyProjectWebHost(this IServiceCollection services)
    {
        // 相同登记重复调用不重复生效：健康检查按名称登记，再追加一次同名检查会让 HealthCheckService 解析失败；
        // 转发头的受信代理也会重复追加。用本入口自己的标记判定，不以某个官方服务是否已注册来推断
        if (services.Any(descriptor => descriptor.ServiceType == typeof(WebHostRegistrationMarker)))
        {
            return services;
        }

        services.AddSingleton<WebHostRegistrationMarker>();
        services.AddHostedService<ApplicationInitializer>();

        // liveness 只表示本进程存活，不依赖外部服务。
        var healthChecks = services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);
#if (RemoteTokenAuth)
        // readiness 在启动时确认 Identity 元数据可达，并锁存结果。
        services.TryAddSingleton<RemoteIdentityReadinessHealthCheck>();
        services.AddHttpClient(nameof(RemoteIdentityReadinessInitializer));
        services.AddHostedService<RemoteIdentityReadinessInitializer>();
        healthChecks.AddCheck<RemoteIdentityReadinessHealthCheck>("remote-identity", tags: ["ready"]);
#else
        healthChecks.AddCheck("ready-self", () => HealthCheckResult.Healthy(), tags: ["ready"]);
#endif

        // HTTP 与 MVC 共用同一 JSON 配置，统一业务响应和 ProblemDetails。
        services.ConfigureHttpJsonOptions(options => WebApiJson.Configure(options.SerializerOptions));
        services.AddControllers()
            .AddJsonOptions(options => WebApiJson.Configure(options.JsonSerializerOptions))
#if (IncludeLocalization)
            // DataAnnotations 使用 ApiResource 的 JSON 资源。
            .AddDataAnnotationsLocalization(options =>
                options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(ApiResource)))
#endif
            .ConfigureApiValidation();
        // 官方 OpenAPI 文档：控制器与组件端点都经 ApiExplorer 收录，只在 Development 映射（见 Program 的 MapOpenApi）
        services.AddOpenApi();

        // X-Forwarded-Host 影响租户解析和绝对 URL，因此只信任显式配置的代理。
        // 在 Options 回调内读取 Build 阶段已合并的最终配置。
        services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>(ConfigureForwardedHeaders);

        services.AddCors();
        services.AddOptions<CorsOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            // 只用于前端部署在另一个源的形态；本机开发经前端开发服务器转发，同源，不需要跨域
            var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials();
#if (LocalIdentity && IncludeMultiTenancy)

                // 跨域前端需要读取租户失效响应头以触发会话恢复。
                policy.WithExposedHeaders(TenantSessionRecoveryOptions.DefaultTenantInvalidHeader);
#endif

                if (allowedOrigins.Length > 0)
                {
                    policy.WithOrigins(allowedOrigins);
                }
            });
        });

#if (SpaFrontend)
        services.AddBrowserOriginProtection(options =>
        {
            options.WritePaths = ["/api"];
#if (IncludeNotifications || IncludeRealTime)
            options.AllMethodPaths = ["/hubs"];
#endif
        });
#endif

        return services;
    }

    // 独立标记区分"本入口已注册过"与宿主自行添加的同类服务
    private sealed class WebHostRegistrationMarker;

    private static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                                   ForwardedHeaders.XForwardedProto |
                                   ForwardedHeaders.XForwardedHost;
        options.ForwardLimit = 1;

        var forwardedConfig = configuration.GetSection("ForwardedHeaders");

        foreach (var proxy in forwardedConfig.GetSection("KnownProxies").Get<string[]>() ?? [])
        {
            if (!IPAddress.TryParse(proxy, out var address))
            {
                throw new InvalidOperationException(
                    $"ForwardedHeaders:KnownProxies contains an invalid IP address: '{proxy}'.");
            }

            options.KnownProxies.Add(address);
        }

        foreach (var network in forwardedConfig.GetSection("KnownNetworks").Get<string[]>() ?? [])
        {
            if (!IPNetwork.TryParse(network, out var parsed))
            {
                throw new InvalidOperationException(
                    $"ForwardedHeaders:KnownNetworks contains an invalid CIDR range: '{network}'.");
            }

            options.KnownIPNetworks.Add(parsed);
        }
    }
}
