using CompanyName.ProjectName.Api.Auth.Authentication;
#if (SpaFrontend)
using Leistd.Security.AspNetCore;
using CompanyName.ProjectName.Api.Options;
using CompanyName.ProjectName.Application.Shared;
using CompanyName.ProjectName.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
#endif

namespace CompanyName.ProjectName.Api.Auth;

/// <summary>认证方案的注册入口：按服务形态组装会话 Cookie、Bearer 与外部登录。</summary>
public static class AuthenticationExtensions
{
    /// <summary>注册本服务形态的认证方案、浏览器会话的服务端票据与会话 Cookie 选项。</summary>
    public static WebApplicationBuilder AddMyProjectAuthentication(this WebApplicationBuilder builder)
    {
#if (SpaFrontend)
        AddBrowserSession(builder);
#endif
#if (ExternalLogin)
        builder.Services.AddExternalAuthentication();
#endif
#if (LocalIdentity)
        builder.AddLocalSessionAuthentication();
#else
        builder.AddRemoteTokenAuthentication();
#endif
        return builder;
    }
#if (SpaFrontend)

    // 浏览器只持有会话引用，票据（含 OAuth 令牌）经 Data Protection 保护后留在分布式缓存
    private static void AddBrowserSession(WebApplicationBuilder builder)
    {
        builder.Services.AddMyProjectDataProtection(builder.Configuration, builder.Environment);
        builder.Services.AddDistributedTicketStore(AuthenticationSchemeNames.SessionCookie,
            options => options.KeyPrefix = "CompanyName.ProjectName:AuthTicket:");
        builder.Services.AddOptions<SessionCookieOptions>()
            .BindConfiguration(SessionCookieOptions.SectionName)
            .Validate(options => options.ExpireDays is >= 1 and <= 365,
                $"{SessionCookieOptions.SectionName}:ExpireDays must be between 1 and 365.")
            .ValidateOnStart();
        builder.Services.AddOptions<CookieAuthenticationOptions>(AuthenticationSchemeNames.SessionCookie)
            .Configure<IOptions<SessionCookieOptions>>((cookie, sessionCookie) =>
            {
                // 滑动过期取会话时长。本地身份签发时改按租户的会话空闲时限（见 LocalSessionAuthenticationExtensions），
                // 这里是部署基线与资源服务形态的值
                cookie.ExpireTimeSpan = sessionCookie.Value.Lifetime;
                cookie.SlidingExpiration = true;
                // 默认 Lax；放宽前先看部署文档的 SameSite 说明（协议端点的跨站进入不需要放宽）。
                // 只作用于应用会话 Cookie，不覆盖协议 correlation/nonce Cookie
                cookie.Cookie.SameSite = sessionCookie.Value.SameSite ?? SameSiteMode.Lax;
            });
    }
#endif
}
