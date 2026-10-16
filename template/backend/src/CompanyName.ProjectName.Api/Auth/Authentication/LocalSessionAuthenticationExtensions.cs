#if (LocalIdentity)
#if (Email)
using Leistd.Security.OneTimeCodes.VerificationCodes;
#endif
using CompanyName.ProjectName.Api.Auth.Sessions;
#if (!IncludeMultiTenancy)
using CompanyName.ProjectName.Api.Middlewares;
#endif
using CompanyName.ProjectName.Api.Options;
using Leistd.Security.AspNetCore;
using CompanyName.ProjectName.Application.Auth.Sessions;
using CompanyName.ProjectName.Application.Shared;
using CompanyName.ProjectName.Domain.Auth.Options;
using CompanyName.ProjectName.Application.Users.Options;
#if (!IncludeMultiTenancy)
using Leistd.Security.Claims;
#endif
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
#if (OpenIddictServer)
using OpenIddict.Validation.AspNetCore;
#endif

namespace CompanyName.ProjectName.Api.Auth.Authentication;

/// <summary>本地身份的账号配置与认证：服务端登记的会话 Cookie（签发形态另有 Bearer）。</summary>
internal static class LocalSessionAuthenticationExtensions
{
    public static void AddLocalSessionAuthentication(this WebApplicationBuilder builder)
    {
        AddAccountOptions(builder.Services, builder.Configuration);

        // 会话空闲时限的部署基线取自会话 Cookie 有效期；租户可在设置里各自调整（Security.SessionIdleTimeoutDays）
        builder.Services.AddOptions<UserSessionOptions>()
            .Configure<IOptions<SessionCookieOptions>>((options, sessionCookie) => options.IdleTimeout = sessionCookie.Value.Lifetime);
        builder.Services.AddRequestClientInfo();
        builder.Services.TryAddTransient<SessionCookieIssuer>();

        builder.Services.AddAuthentication(options =>
        {
#if (OpenIddictServer)
            // 多租户解析前必须按请求恢复 Bearer 或 Cookie 主体，防止请求头改写已登录用户的租户。
            options.DefaultAuthenticateScheme = AuthenticationSchemeNames.Smart;
            // 挑战（及回落到它的禁止）与认证同一分流：带 Authorization 头由 OpenIddict 回应，否则由会话 Cookie 写 401/403。
            // 不指定方案的策略（组件端点）匿名被拒走默认挑战；若交给 OpenIddict，它对未经它认证的请求答 insufficient_access（403）
            options.DefaultChallengeScheme = AuthenticationSchemeNames.Smart;
#else
            // 不签发令牌时只恢复 Cookie 会话。
            options.DefaultAuthenticateScheme = AuthenticationSchemeNames.SessionCookie;
            options.DefaultChallengeScheme = AuthenticationSchemeNames.SessionCookie;
#endif
        })
#if (OpenIddictServer)
        .AddPolicyScheme(AuthenticationSchemeNames.Smart, "Selects Bearer or Cookie per request", options =>
        {
            options.ForwardDefaultSelector = context =>
                context.Request.Headers.ContainsKey("Authorization")
                    ? OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme
                    : AuthenticationSchemeNames.SessionCookie;
        })
#endif
        .AddCookie(AuthenticationSchemeNames.SessionCookie, options =>
        {
            var isDevelopmentEnvironment = builder.Environment.IsDevelopment();

            options.LoginPath = "/auth/login";
            // 部署环境用 __Host-Http- 前缀（RFC 10017 §6.1.3.2）：浏览器只接受经 HTTPS、Path=/、不带 Domain、
            // 由 HTTP 响应写入的这个名字。开发环境允许 HTTP 同源调试，而前缀要求 Secure，因此不带前缀。
            options.Cookie.Name = isDevelopmentEnvironment ? "CompanyName.ProjectName.Auth" : "__Host-Http-CompanyName.ProjectName.Auth";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = isDevelopmentEnvironment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Cookie.IsEssential = true;

            // 每个请求确认业务会话仍有效，服务端票据存储另负责 Cookie 引用的撤销。
            //
            // 撤销（退出其他设备、改密码）才能对已发出的 Cookie 生效。结果带短缓存，见 IUserSessionValidator
            options.Events.OnValidatePrincipal = async context =>
            {
#if (!IncludeMultiTenancy)
                if (!HostPrincipalMiddleware.IsHost(context.Principal, context.HttpContext.RequestServices.GetRequiredService<IOptions<ClaimTypeOptions>>().Value))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(AuthenticationSchemeNames.SessionCookie);
                    return;
                }
#endif
                var validator = context.HttpContext.RequestServices.GetRequiredService<IUserSessionValidator>();
                var validation = context.Principal is null
                    ? UserSessionValidation.Invalid
                    : await validator.ValidateAsync(context.Principal, context.HttpContext.RequestAborted);
                if (!validation.IsValid)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(AuthenticationSchemeNames.SessionCookie);
                    return;
                }

                // 租户调整了会话空闲时限：把票据有效期收敛到新时长再续发（官方滑动续期按签发与到期之差续期）。
                // 只在校验器这次实际查过会话时比对，用的是它按主体租户读到的值；缓存命中期间不另查
                if (validation.IdleTimeout is { } idleTimeout
                    && context.Properties.ExpiresUtc - context.Properties.IssuedUtc != idleTimeout)
                {
                    SetLifetime(context.Properties, idleTimeout, context.Options.TimeProvider ?? TimeProvider.System);
                    context.ShouldRenew = true;
                }
            };

            // 会话 Cookie 的有效期取主体所在租户的空闲时限（模拟登录进出时环境租户与主体不同，按主体读）；
            // 租户不可用时沿用部署基线 ExpireTimeSpan，交给租户会话自恢复处理
            options.Events.OnSigningIn = async context =>
            {
                var reader = context.HttpContext.RequestServices.GetRequiredService<ISessionIdleTimeoutReader>();
                if (context.Principal is not null
                    && await reader.ReadAsync(context.Principal, context.HttpContext.RequestAborted) is { } idleTimeout)
                {
                    SetLifetime(context.Properties, idleTimeout, context.Options.TimeProvider ?? TimeProvider.System);
                }
            };

            // 只改票据时序，不动 auth_time 等认证时间声明
            static void SetLifetime(AuthenticationProperties properties, TimeSpan idleTimeout, TimeProvider time)
            {
                var now = time.GetUtcNow();
                properties.IssuedUtc = now;
                properties.ExpiresUtc = now + idleTimeout;
            }

            // API 拒绝必须返回 401/403，不能被 Cookie 重定向和 SPA 回退转换为 HTML 200。
            options.Events.OnRedirectToLogin = context => WriteStatusAsync(context, StatusCodes.Status401Unauthorized);
            options.Events.OnRedirectToAccessDenied = context => WriteStatusAsync(context, StatusCodes.Status403Forbidden);

            static Task WriteStatusAsync(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
            {
                context.Response.StatusCode = statusCode;
                return Task.CompletedTask;
            }
        });
    }

    // 账号相关的部署基线：管理员引导、邮箱验证码密钥、注册策略
    private static void AddAccountOptions(IServiceCollection services, IConfiguration configuration)
    {
        // 口令不在启动期校验：只有真的要创建管理员时才需要，由初始化器在那一刻按口令策略校验并报出键名
        services.AddOptions<DefaultAdminOptions>()
            .Bind(configuration.GetSection(DefaultAdminOptions.SectionName));

#if (Email)
        // 邮箱验证开启时，HMAC 密钥必须跨实例和重启稳定。
        services.AddOptions<VerificationCodeOptions>()
            .Validate<IConfiguration>(
                (options, config) =>
                    !config.GetValue<bool>("UserRegistration:EnableEmailVerification")
                    || options.IsKeyUsable,
                $"{VerificationCodeOptions.SectionName}:Key is required when " +
                "UserRegistration:EnableEmailVerification is true, and must be at least " +
                $"{VerificationCodeOptions.MinimumKeyBytes} base64-encoded bytes.")
            .ValidateOnStart();
#endif
        services.AddOptions<UserRegistrationOptions>()
            .Bind(configuration.GetSection(UserRegistrationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }
}
#endif
