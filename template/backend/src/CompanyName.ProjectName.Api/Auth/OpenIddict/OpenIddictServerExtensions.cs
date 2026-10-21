#if (OpenIddictServer)
using CompanyName.ProjectName.Application.Auth.OAuth;
using CompanyName.ProjectName.Api.Options;
using CompanyName.ProjectName.Application.Auth.Options;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.BackgroundJobs;
using Leistd.BackgroundJobs.Recurring;
using Leistd.Security.OpenIddict.Server;
using Leistd.Security.OpenIddict.Server.Pruning;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace CompanyName.ProjectName.Api.Auth.OpenIddict;

/// <summary>授权服务器（OpenIddict Server）与本地令牌校验的注册入口。</summary>
public static class OpenIddictServerExtensions
{
    /// <summary>注册 OAuth 选项、退出确认所需的防伪与交互凭据，以及 OpenIddict 的存储、签发端与本地校验。</summary>
    public static WebApplicationBuilder AddMyProjectOpenIddictServer(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<OAuthServerOptions>, OAuthServerOptionsValidator>());
        builder.Services.AddOptions<OAuthServerOptions>()
            .Bind(builder.Configuration.GetSection(OAuthServerOptions.SectionName))
            .ValidateOnStart();
        // 退出确认是本源表单 POST，用官方防伪令牌校验；Cookie 规则与会话 Cookie 一致（部署环境 __Host- 前缀、仅 HTTPS）
        builder.Services.AddAntiforgery(options =>
        {
            var isDevelopmentEnvironment = builder.Environment.IsDevelopment();
            options.Cookie.Name = isDevelopmentEnvironment ? "CompanyName.ProjectName.Antiforgery" : "__Host-CompanyName.ProjectName.Antiforgery";
            options.Cookie.Path = "/";
            options.Cookie.SecurePolicy = isDevelopmentEnvironment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
        builder.Services.TryAddSingleton<ConnectInteractionProtector>();

        // 组合期读取并校验：下面 AddServer 的回调要到首次解析 OpenIddict 选项时才执行，那时才报错已经晚了。
        // 默认必须显式提供证书：开发证书生成在运行用户的证书存储里、每台机器各一份，多副本互不认，
        // 重建容器后已签发的令牌全部失效，只适合本机开发（由 appsettings.Development.json 打开）。
        var oauthOpts = builder.Configuration.GetSection(OAuthServerOptions.SectionName).Get<OAuthServerOptions>() ?? new OAuthServerOptions();
        // 与启动期校验同一个验证器：这里更早，是因为下面组合 OpenIddict 时就要加载证书
        if (new OAuthServerOptionsValidator().Validate(null, oauthOpts) is { Failed: true } oauthValidation)
            throw new OptionsValidationException(OAuthServerOptions.SectionName, typeof(OAuthServerOptions), oauthValidation.Failures);
        // 证书在组合期逐张加载：路径缺失、文件损坏或口令错误时报出带下标的键名，而不是首个请求时的笼统异常
        var signingCertificates = oauthOpts.UseDevelopmentCertificates ? []
            : OAuthCertificateLoader.Load(oauthOpts.SigningCertificates, "OAuth:SigningCertificates");
        var encryptionCertificates = oauthOpts.UseDevelopmentCertificates ? []
            : OAuthCertificateLoader.Load(oauthOpts.EncryptionCertificates, "OAuth:EncryptionCertificates");

        var resources = builder.Configuration.GetSection(OAuthResourceOptions.SectionName).Get<OAuthResourceOptions>() ?? new OAuthResourceOptions();
        if (new OAuthResourceOptionsValidator().Validate(null, resources) is { Failed: true } resourceValidation)
            throw new OptionsValidationException(OAuthResourceOptions.SectionName, typeof(OAuthResourceOptions), resourceValidation.Failures);
        var oauthScopes = OAuthScopes.All(resources);

        builder.Services.AddOpenIddict()
            .AddCore(options =>
            {
                options.UseEntityFrameworkCore()
                    .UseDbContext<OpenIddictDbContext>();
            })
            .AddServer(options =>
            {
                options.SetAuthorizationEndpointUris("/connect/authorize")
                    .SetTokenEndpointUris("/connect/token")
                    .SetUserInfoEndpointUris("/connect/userinfo")
                    .SetEndSessionEndpointUris("/connect/logout");
                options.SetAccessTokenLifetime(oauthOpts.AccessTokenLifetime);

                if (!string.IsNullOrWhiteSpace(oauthOpts.Issuer))
                {
                    options.SetIssuer(new Uri(oauthOpts.Issuer));
                }

                options.AllowAuthorizationCodeFlow()
                    .RequireProofKeyForCodeExchange();
                // 授权与退出请求先存为 request token（控制库）再以 request_uri 重入：依赖方可以跨站 POST 发起，
                // 重入是顶层 GET，Lax 会话 Cookie 随之送达；id_token_hint 也不再留在浏览器地址里。
                options.EnableAuthorizationRequestCaching()
                    .EnableEndSessionRequestCaching();
                options.AllowRefreshTokenFlow();
                options.AllowClientCredentialsFlow();
                options.AllowTokenExchangeFlow();
                options.Configure(server =>
                {
                    server.SubjectTokenTypes.Clear();
                    server.SubjectTokenTypes.Add(OpenIddictConstants.TokenTypeIdentifiers.AccessToken);
                    server.ActorTokenTypes.Clear();
                    server.RequestedTokenTypes.Clear();
                    server.RequestedTokenTypes.Add(OpenIddictConstants.TokenTypeIdentifiers.AccessToken);
                });
                options.RemoveEventHandler(OpenIddictServerHandlers.Exchange.ValidateAuthorizedParty.Descriptor);
                options.AddEventHandler<OpenIddictServerEvents.ValidateTokenRequestContext>(handler =>
                    handler.UseScopedHandler<ResourceOwnerAuthorizedPartyHandler>()
                        .SetOrder(OpenIddictServerHandlers.Exchange.ValidateAuthorizedParty.Descriptor.Order));
                options.RegisterAudiences(oauthScopes.SelectMany(scope => scope.Resources).Distinct().ToArray());

                // 跨服务用签名 JWT：资源服务经 discovery/JWKS 验签，无需分发解密密钥；claim 对持有者可读（见 auth.md 用户认证小节）。
                options.DisableAccessTokenEncryption();

                options.RegisterScopes(oauthScopes.Select(scope => scope.Name).ToArray());

                if (oauthOpts.UseDevelopmentCertificates)
                {
                    options.AddDevelopmentEncryptionCertificate()
                        .AddDevelopmentSigningCertificate();
                }
                else
                {
                    // 全部登记：重叠轮换期间新旧证书同时发布进 JWKS，旧证书签发或加密的令牌仍可验证、解密
                    foreach (var certificate in encryptionCertificates) options.AddEncryptionCertificate(certificate);
                    foreach (var certificate in signingCertificates) options.AddSigningCertificate(certificate);
                }

                var aspNetCore = options.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough();
                if (oauthOpts.DisableHttpsRequirement)
                {
                    aspNetCore.DisableTransportSecurityRequirement();
                }
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                // 只接受签给本服务 API 的令牌：签给下游 API 的令牌（受众是那个 API）不能用来调用这里
                options.AddAudiences(resources.Resource);

                // 每个请求按令牌记录确认令牌未被撤销：停用、删除账号时撤销的令牌立即失效，
                // 在认证阶段就以 invalid_token 拒绝。API 与授权服务器同库部署，这次查库替代了逐请求查用户
                options.EnableTokenEntryValidation();

                // 普通 API 只接受 Bearer 头，避免令牌进入 URL 和访问日志。
                options.UseAspNetCore()
                       .DisableAccessTokenExtractionFromQueryString()
                       .DisableAccessTokenExtractionFromBodyForm();
            });

        builder.Services.AddTokenExchangeExpirationLimit();
        builder.Services.AddOpenIddictPruning();
        builder.Services.AddRecurringJob<OpenIddictPruningJob>(OpenIddictPruningJob.Name,
            RecurringJobSchedule.DailyAt(new TimeOnly(3, 30)), RecurringJobScope.Cluster);

        return builder;
    }
}
#endif
