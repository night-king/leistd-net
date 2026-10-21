#if (RemoteTokenAuth && !IncludeMultiTenancy)
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.Security.Claims;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Validation;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>真实 Bearer 验签：签发方、受众、有效期或签名不符的令牌被拒；验签通过后，宿主入口在投影和业务访问前拒绝租户声明。</summary>
public sealed class ResourceHostPrincipalTests
{
    private const string Issuer = "https://identity.test/";
    private const string Audience = "resource-api";

    /// <summary>签发方与受众取自部署配置（Authentication:Issuer、Authentication:Audience），有效期与签名由官方验证处理器判定。</summary>
    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("unsigned")]
    public async Task Tokens_failing_validation_are_rejected_before_projection(string kind)
    {
        using var factory = new ProjectWebApplicationFactory { UseProductionAuthentication = true };
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "token-validation-test" };
        using var host = Host(factory, key);
        var subject = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var token = kind switch
        {
            "issuer" => Token(subject, key, null, issuer: "https://other-identity.test/"),
            "audience" => Token(subject, key, null, audience: "other-api"),
            "expired" => Token(subject, key, null, issuedAt: now.AddMinutes(-20), expires: now.AddMinutes(-10)),
            "unsigned" => Token(subject, null, null),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var rejected = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using var dbScope = host.Services.CreateScope();
        var db = dbScope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        Assert.False(await db.Users.IgnoreQueryFilters().AnyAsync(user => user.Id == subject));
        // 同一宿主接受合法令牌，拒绝才归因于被改的那一处
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(subject, key, null));
        using var valid = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("malformed")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("conflicting")]
    public async Task Invalid_scope_is_rejected_before_projection_for_api_and_hub(string kind)
    {
        using var factory = new ProjectWebApplicationFactory { UseProductionAuthentication = true };
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "host-scope-test" };
        using var host = Host(factory, key);
        var subject = Guid.NewGuid();
        var tenant = Guid.NewGuid().ToString();
        object scope = kind switch
        {
            "tenant" => tenant,
            "malformed" => "not-a-guid",
            "empty" => "",
            "duplicate" => new[] { tenant, tenant },
            "conflicting" => new[] { tenant, Guid.NewGuid().ToString() },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var token = Token(subject, key, scope);
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var api = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
#if (IncludeNotifications || IncludeRealTime)
#if (IncludeRealTime)
        const string hub = "/hubs/realtime/negotiate?negotiateVersion=1";
#else
        const string hub = "/hubs/notifications/negotiate?negotiateVersion=1";
#endif
        using var negotiation = await client.PostAsync(hub, null);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiation.StatusCode);
#endif
        using var dbScope = host.Services.CreateScope();
        var db = dbScope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        Assert.False(await db.Users.IgnoreQueryFilters().AnyAsync(user => user.Id == subject));
        // 同一真实验签配置必须接受宿主，防止“全部拒绝”造成假绿。
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(subject, key, null));
        using var valid = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.True(await db.Users.IgnoreQueryFilters().AnyAsync(user => user.Id == subject));
    }

    // 签发方与受众走部署配置；只把签发方元数据换成本地密钥，验证处理器仍是生产注册的那一套
    private static WebApplicationFactory<Program> Host(ProjectWebApplicationFactory factory, SecurityKey key) =>
        factory.WithWebHostBuilder(builder => builder.UseSetting("Authentication:Audience", Audience)
            .ConfigureTestServices(services => services.Configure<OpenIddictValidationOptions>(options =>
            {
                options.Configuration = new OpenIddictConfiguration { Issuer = new Uri(Issuer) };
                options.Configuration.SigningKeys.Add(key);
            })));

    private static string Token(
        Guid subject, SecurityKey? key, object? tenant,
        string issuer = Issuer, string audience = Audience, DateTime? issuedAt = null, DateTime? expires = null)
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject.ToString(), ["jti"] = Guid.NewGuid().ToString() };
        if (tenant is not null) claims[CustomClaimTypes.TenantId] = tenant;
        var issued = issuedAt ?? DateTime.UtcNow.AddSeconds(-5);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer, Audience = audience, Claims = claims, TokenType = "at+jwt",
            IssuedAt = issued, NotBefore = issued, Expires = expires ?? DateTime.UtcNow.AddMinutes(5),
            // 不给密钥时签出 alg=none 的未签名令牌
            SigningCredentials = key is null ? null : new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        });
    }
}
#endif
