#if (RemoteTokenAuth)
using CompanyName.ProjectName.Api.Options;
#if (ResourceBrowserSession)
using CompanyName.ProjectName.Api.Auth.Sessions;
using CompanyName.ProjectName.Application.Shared;
#endif
using Leistd.ServiceClient.AspNetCore;
using Leistd.Security.OpenIddict.Validation;
#if (ResourceBrowserSession)
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
#endif
#if (ResourceBrowserSession)
using Microsoft.Extensions.DependencyInjection.Extensions;
#endif
using Microsoft.Extensions.Options;
using OpenIddict.Validation;
using OpenIddict.Validation.AspNetCore;

namespace CompanyName.ProjectName.Api.Auth.Authentication;

/// <summary>资源服务的认证：远端签发方的 Bearer 校验，以及（带浏览器会话时）服务端 OIDC 会话。</summary>
internal static class RemoteTokenAuthenticationExtensions
{
    private const string RemoteIdentityConfigurationError =
        "Resource services require Authentication:Issuer (an absolute http(s) URI) and Authentication:Audience.";

    public static void AddRemoteTokenAuthentication(this WebApplicationBuilder builder)
    {
        // Resource 的机器 Bearer 与服务端 OIDC 会话使用同一签发方和资源受众。
        // 组合期不读这些值：缺失或格式错误由启动期校验报出键名，OpenIddict 与 OIDC 处理器在解析选项时才取值
        builder.Services.AddOptions<RemoteIdentityOptions>()
            .Bind(builder.Configuration.GetSection(RemoteIdentityOptions.SectionName))
            .Validate(options => options.IsUsable, RemoteIdentityConfigurationError)
#if (ResourceBrowserSession)
            .Validate(options => !string.IsNullOrWhiteSpace(options.ClientId), ConfidentialClientConfigurationError("ClientId"))
            .Validate(options => !string.IsNullOrWhiteSpace(options.ClientSecret), ConfidentialClientConfigurationError("ClientSecret"))
#endif
            .ValidateOnStart();

        builder.Services.AddOpenIddict()
            .AddValidation(options =>
            {
                options.UseSystemNetHttp();
                options.UseAspNetCore()
                    .DisableAccessTokenExtractionFromQueryString()
                    .DisableAccessTokenExtractionFromBodyForm();
            });
        // 签发方与受众在 OpenIddict 按 issuer 建配置管理器（PostConfigure）之前写入
        builder.Services.AddOptions<OpenIddictValidationOptions>()
            .Configure<IOptions<RemoteIdentityOptions>>((options, remoteIdentityOptions) =>
            {
                var remoteIdentity = remoteIdentityOptions.Value;
                if (!remoteIdentity.IsUsable)
                {
                    // 启动期校验会拒绝这份配置并报出键名
                    return;
                }

                options.Issuer = remoteIdentity.IssuerUri;
                options.Audiences.Add(remoteIdentity.Audience!);
            });
        builder.Services.AddSigningKeyRefresh();

        // 下游 Token Exchange 取经过验证的请求令牌：会话取服务端票据里的，Bearer 取验证后的
#if (ResourceBrowserSession)
        builder.Services.AddAuthenticatedUserAccessTokenAccessor(AuthenticationSchemeNames.Smart);
#else
        builder.Services.AddUserAccessTokenAccessor(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
#endif

#if (ResourceBrowserSession)
        builder.AddResourceBrowserSession();
#else
        // 纯资源 API：只接受 Bearer。没有浏览器会话，也就没有 Cookie、OIDC 客户端与登录退出端点
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
        });
#endif
    }
#if (ResourceBrowserSession)

    private static string ConfidentialClientConfigurationError(string key) =>
        $"Authentication:{key} is required for the Resource OIDC confidential client.";

    // 有 Authorization 头只选 Bearer，失败不回退 Cookie；无头时选服务端 Cookie 会话，由机密 OIDC 客户端登录
    private static void AddResourceBrowserSession(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton<ResourceSessionRefresher>();
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = AuthenticationSchemeNames.Smart;
            options.DefaultChallengeScheme = AuthenticationSchemeNames.Smart;
        })
        .AddPolicyScheme(AuthenticationSchemeNames.Smart, "Selects the request authentication scheme", options =>
            options.ForwardDefaultSelector = context => context.Request.Headers.ContainsKey("Authorization")
                ? OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme : AuthenticationSchemeNames.SessionCookie)
        .AddCookie(AuthenticationSchemeNames.SessionCookie, options =>
        {
            // 前缀取舍同 LocalIdentity 的会话 Cookie；时长与站点策略见 AuthenticationExtensions 的会话 Cookie 选项。
            options.Cookie.Name = builder.Environment.IsDevelopment() ? "CompanyName.ProjectName.Auth" : "__Host-Http-CompanyName.ProjectName.Auth";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Events.OnValidatePrincipal = context => context.HttpContext.RequestServices.GetRequiredService<ResourceSessionRefresher>().ValidateAsync(context);
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        })
        .AddOpenIdConnect(AuthenticationSchemeNames.OpenIdConnect, options =>
        {
            // 签发方、客户端与 scope 见下方按 RemoteIdentityOptions 的延后配置
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            options.SignInScheme = AuthenticationSchemeNames.SessionCookie;
            options.ResponseType = "code";
            options.SaveTokens = true;
            options.MapInboundClaims = false;
            // 会话主体只由 ValidateAccessTokenAsync 构建（登录在 OnTokenValidated，续期在 ResourceSessionRefresher），
            // 两条路同源。处理器的默认 ClaimActions 是给 id_token/userinfo 做映射的，却会在 OnTokenValidated 之后
            // 对换上的访问令牌主体再跑一遍，删掉 acr、aud、iss、exp 等——只删登录这一条路，首次续期后它们又回来
            options.ClaimActions.Clear();
            options.CallbackPath = "/api/v1/auth/signin";
            // 授权与退出请求都以自动提交的表单 POST 发往 Identity：id_token_hint 不进地址栏、历史记录与 Referer
            options.AuthenticationMethod = OpenIdConnectRedirectBehavior.FormPost;
            options.SignedOutCallbackPath = "/api/v1/auth/signout";
            options.Events.OnRemoteFailure = context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return Task.CompletedTask;
            };
            options.Events.OnTokenValidated = async context =>
            {
                // tenant_id 等资源声明在签发方的访问令牌中，不能假定 ID token 也带这些字段。
                context.Principal = await ResourceSessionRefresher.ValidateAccessTokenAsync(context.HttpContext,
                    context.TokenEndpointResponse!.AccessToken, context.HttpContext.RequestAborted);
            };
            options.Events.OnRedirectToIdentityProviderForSignOut = context =>
            {
                // 保留 id_token_hint：Identity 据其中的会话标识判断能否免确认退出；client_id 在 hint 缺失时关联退出回调
                context.ProtocolMessage.ClientId = context.Options.ClientId;
                return Task.CompletedTask;
            };
        });
        builder.Services.AddOptions<OpenIdConnectOptions>(AuthenticationSchemeNames.OpenIdConnect)
            .Configure<IOptions<RemoteIdentityOptions>>((options, remoteIdentityOptions) =>
            {
                var remoteIdentity = remoteIdentityOptions.Value;
                options.Authority = remoteIdentity.Issuer;
                options.ClientId = remoteIdentity.ClientId;
                options.ClientSecret = remoteIdentity.ClientSecret;
                options.Scope.Clear();
                foreach (var scope in new[] { "openid", "profile", "email", "roles", "offline_access", remoteIdentity.Scope ?? remoteIdentity.Audience })
                    if (!string.IsNullOrWhiteSpace(scope)) options.Scope.Add(scope);
            });
    }
#endif
}
#endif
