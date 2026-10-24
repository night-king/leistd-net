#if (OpenIddictServer || RemoteTokenAuth)
using CompanyName.ProjectName.Api;
using Leistd.Security.AspNetCore;
using Leistd.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Validation;
using OpenIddict.Validation.AspNetCore;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;

namespace CompanyName.ProjectName.UnitTests.Api;

/// <summary><c>AddMachineScopePolicy</c> 登记的策略：只放行经 Bearer 认证、带指定 scope 的机器主体。</summary>
/// <remarks>
/// 不起宿主：策略只依赖主体标识的 claim 配置与 OpenIddict 的 Bearer 校验方案，两者在这里按生产的方案名组装，
/// 请求经认证中间件再按策略判定，与授权中间件同一条路。令牌经真实验签进入策略，scope claim 的形状与生产一致；
/// 租户控制面的机器端点另有走完整 HTTP 的用例。
/// </remarks>
public sealed class MachineScopePolicyTests : IDisposable
{
    private const string PolicyName = "test.orders-machine";
    // 服务级 scope：内部服务间机器调用的默认粒度
    private const string ServiceScope = "orders-api";
    private const string Machine = "client:orders-worker";
    private const string NaturalPerson = "8f14e45f-ea6a-4c4b-9b2b-7c1f0a2d3e4f";
    private const string Issuer = "https://identity.test/";
    private const string Audience = "orders-api";
    // 替身会话方案：带这个头时恢复出一个 claim 满足断言的主体，代表会话 Cookie
    private const string SessionScheme = "TestSession";
    private const string SessionHeader = "X-Test-Session";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;

    public MachineScopePolicyTests() => _key = new RsaSecurityKey(_rsa) { KeyId = "machine-scope-test" };

    public void Dispose() => _rsa.Dispose();

    [Theory]
    [InlineData(Machine, ServiceScope, true)]
    // scope claim 是空格分隔的多值
    [InlineData(Machine, "profile orders-api", true)]
    // 不带该 scope 的机器令牌
    [InlineData(Machine, null, false)]
    [InlineData(Machine, "billing-api", false)]
    // 只按整个 scope 比较，同前缀的细分 scope 不算命中
    [InlineData(Machine, "orders-api.read", false)]
    // 用户令牌（含 Token Exchange 令牌）的主体是用户 Id，带同一个 scope 也不放行
    [InlineData(NaturalPerson, ServiceScope, false)]
    public async Task Verified_bearer_tokens_are_admitted_only_for_machines_with_the_required_scope(
        string subject, string? scope, bool expected)
    {
        await using var provider = Services();

        var (succeeded, _) = await EvaluateAsync(provider, Token(subject, scope), session: false);

        Assert.Equal(expected, succeeded);
    }

    [Fact]
    public async Task Super_admin_claim_does_not_admit_the_machine_policy()
    {
        await using var provider = Services();
        var principal = Principal(NaturalPerson, ServiceScope, "test");
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(CustomClaimTypes.IsSuperAdmin, "true"));

        var result = await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(principal, resource: null, PolicyName);

        Assert.False(result.Succeeded);
    }

    /// <summary>会话 Cookie 恢复的主体即使 claim 满足断言也被拒：策略只认 Bearer，没有 Authorization 头就是匿名。</summary>
    [Fact]
    public async Task A_session_principal_without_a_bearer_token_is_rejected()
    {
        await using var provider = Services();
        var policy = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value.GetPolicy(PolicyName)!;

        // 对照：只看 claim 时会话主体满足断言，且认证中间件确实恢复出了它；下面的拒绝来自认证方案的收窄
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        Assert.True((await authorization.AuthorizeAsync(Principal(Machine, ServiceScope, SessionScheme), resource: null, PolicyName)).Succeeded);
        Assert.Equal([OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme], policy.AuthenticationSchemes);

        var (succeeded, restored) = await EvaluateAsync(provider, bearer: null, session: true);

        Assert.True(restored);
        Assert.False(succeeded);
    }

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSecurity();
        services.AddAuthorization();
        services.AddAuthentication(SessionScheme)
            .AddScheme<AuthenticationSchemeOptions, SessionHandler>(SessionScheme, _ => { });
        services.AddOpenIddict().AddValidation(options =>
        {
            options.SetIssuer(Issuer);
            options.AddAudiences(Audience);
            options.UseAspNetCore();
        });
        // 签名密钥直接给出，不回源签发方的发现文档
        services.Configure<OpenIddictValidationOptions>(options =>
        {
            options.Configuration = new OpenIddictConfiguration { Issuer = new Uri(Issuer) };
            options.Configuration.SigningKeys.Add(_key);
        });
        services.AddMachineScopePolicy(PolicyName, ServiceScope);
        return services.BuildServiceProvider();
    }

    private string Token(string subject, string? scope)
    {
        var claims = new Dictionary<string, object>
        {
            [CustomClaimTypes.Subject] = subject,
            [CustomClaimTypes.ClientId] = "orders-worker",
            ["jti"] = Guid.NewGuid().ToString()
        };
        if (scope is not null) claims[OpenIddictConstants.Claims.Scope] = scope;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = Audience, Claims = claims, TokenType = "at+jwt",
            IssuedAt = DateTime.UtcNow, NotBefore = DateTime.UtcNow.AddSeconds(-5), Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256)
        });
    }

    private static ClaimsPrincipal Principal(string subject, string scope, string authenticationType) =>
        new(new ClaimsIdentity(
            [new Claim(OpenIddictConstants.Claims.Subject, subject), new Claim(OpenIddictConstants.Claims.Scope, scope)],
            authenticationType));

    // 先经认证中间件（OpenIddict 在这里建立本次请求的校验上下文，默认方案恢复的主体写进 HttpContext.User），再按策略判定
    private static async Task<(bool Succeeded, bool SessionRestored)> EvaluateAsync(
        IServiceProvider services, string? bearer, bool session)
    {
        await using var scope = services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost");
        if (bearer is not null) context.Request.Headers.Authorization = $"Bearer {bearer}";
        if (session) context.Request.Headers[SessionHeader] = "1";
        var policy = services.GetRequiredService<IOptions<AuthorizationOptions>>().Value.GetPolicy(PolicyName)!;
        var evaluator = scope.ServiceProvider.GetRequiredService<IPolicyEvaluator>();
        bool succeeded = false, restored = false;

        var pipeline = new AuthenticationMiddleware(async http =>
        {
            restored = http.User.Identity?.AuthenticationType == SessionScheme;
            var authentication = await evaluator.AuthenticateAsync(policy, http);
            succeeded = (await evaluator.AuthorizeAsync(policy, authentication, http, resource: null)).Succeeded;
        }, scope.ServiceProvider.GetRequiredService<IAuthenticationSchemeProvider>());
        await pipeline.Invoke(context);

        return (succeeded, restored);
    }

    private sealed class SessionHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(Request.Headers.ContainsKey(SessionHeader)
                ? AuthenticateResult.Success(new AuthenticationTicket(Principal(Machine, ServiceScope, Scheme.Name), Scheme.Name))
                : AuthenticateResult.NoResult());
    }
}
#endif
