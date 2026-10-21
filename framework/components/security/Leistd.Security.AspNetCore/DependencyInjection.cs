using Leistd.Security.AspNetCore.Claims;
using Leistd.Security.AspNetCore.BrowserOrigins;
using Microsoft.Extensions.Options;
using Leistd.Security.AspNetCore.RequestContext;
using Leistd.Security.AspNetCore.Cookies;
using Microsoft.AspNetCore.Authentication.Cookies;
using Leistd.Security.RequestContext;
using Leistd.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Leistd.Security.AspNetCore;

/// <summary>当前主体安全上下文的注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册当前主体、用户和客户端访问器，主体来源为 <c>HttpContext.User</c>。</summary>
    /// <example>
    /// <code>
    /// builder.Services.AddSecurity();
    ///
    /// public class OrderService(ICurrentUser currentUser)
    /// {
    ///     public Guid? OwnerId =&gt; currentUser.Id;
    /// }
    /// </code>
    /// </example>
    /// <remarks>
    /// 在 <c>AddAmbientContext()</c> 的基础上把主体来源换成 <c>HttpContext.User</c>。
    /// 与 <c>AddAmbientContext()</c> 的调用顺序无关。可重复调用，结果与调用一次相同。
    /// </remarks>
    public static IServiceCollection AddSecurity(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddAmbientContext();

        services.Replace(ServiceDescriptor.Singleton<ICurrentPrincipalAccessor, HttpContextCurrentPrincipalAccessor>());

        return services;
    }

    /// <summary>注册请求客户端信息的 HTTP 实现。</summary>
    /// <remarks>默认实现为 Transient，读取调用时的上下文；重复登记幂等，不覆盖宿主实现。</remarks>
    public static IServiceCollection AddRequestClientInfo(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddTransient<IRequestClientInfo, HttpRequestClientInfo>();
        return services;
    }

    /// <summary>为命名 Cookie 方案配置服务端票据，保留宿主先注册的 ITicketStore。</summary>
    /// <remarks>
    /// 宿主提供缓存、数据保护与锁。存储为单例，各方案共享配置；重复登记同一方案只挂载一次，配置委托仍叠加。
    /// 组件在 PostConfigure 包装该方案的 Events/EventsType/CookieManager，之后整体替换它们会在启动时失败；后置改 OnXxx 回调有效。
    /// </remarks>
    public static IServiceCollection AddDistributedTicketStore(
        this IServiceCollection services,
        string authenticationScheme,
        Action<DistributedTicketStoreOptions>? configure = null,
        string configSectionPath = DistributedTicketStoreOptions.SectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationScheme);
        ArgumentException.ThrowIfNullOrWhiteSpace(configSectionPath);
        var options = services.AddOptions<DistributedTicketStoreOptions>().BindConfiguration(configSectionPath);
        if (configure is not null) options.Configure(configure);
        options.Validate(value => !string.IsNullOrWhiteSpace(value.KeyPrefix), $"{configSectionPath}:KeyPrefix is required.")
            .Validate(value => value.FallbackLifetime > TimeSpan.Zero, $"{configSectionPath}:FallbackLifetime must be positive.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ITicketStore, DistributedTicketStore>();
        var registration = services.FirstOrDefault(item => item.ServiceType == typeof(TicketStoreRegistration))?.ImplementationInstance as TicketStoreRegistration;
        if (registration is null)
        {
            registration = new TicketStoreRegistration();
            services.AddSingleton(registration);
            services.AddSingleton<IValidateOptions<CookieAuthenticationOptions>>(new TicketCookieOptionsValidator(registration.Schemes));
        }
        if (registration.Schemes.Add(authenticationScheme))
            services.AddOptions<CookieAuthenticationOptions>(authenticationScheme)
                .Configure<ITicketStore>((cookie, store) => cookie.SessionStore = store)
                .PostConfigure(DistributedTicketStore.ConfigureCookie)
                .ValidateOnStart();
        return services;
    }

    /// <summary>注册显式路径的来源防护参数；宿主提供原生 CORS，按需调用 UseBrowserOriginProtection。</summary>
    /// <remarks>先绑定后应用委托，启动验证；至少选择一个保护路径，不随 AddSecurity 自动开启。</remarks>
    public static IServiceCollection AddBrowserOriginProtection(this IServiceCollection services,
        Action<BrowserOriginProtectionOptions>? configure = null,
        string configSectionPath = BrowserOriginProtectionOptions.SectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configSectionPath);
        services.TryAddSingleton<BrowserOriginRegistration>();
        var options = services.AddOptions<BrowserOriginProtectionOptions>().BindConfiguration(configSectionPath);
        if (configure is not null) options.Configure(configure);
        services.AddSingleton<IValidateOptions<BrowserOriginProtectionOptions>>(new BrowserOriginProtectionOptionsValidator(configSectionPath));
        options.ValidateOnStart();
        return services;
    }

    private sealed class TicketStoreRegistration
    {
        public HashSet<string> Schemes { get; } = new(StringComparer.Ordinal);
    }
}
