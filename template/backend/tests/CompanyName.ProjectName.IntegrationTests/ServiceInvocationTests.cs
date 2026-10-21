#if (OpenIddictServer || RemoteTokenAuth)
using CompanyName.ProjectName.Client;
using Leistd.ServiceClient.Exceptions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
#if (OpenIddictServer)
using CompanyName.ProjectName.Application.Auth.Options;
using CompanyName.ProjectName.Api.Options;
using Leistd.ServiceClient.OAuth;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using OpenIddict.Client.SystemNetHttp;
#else
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CompanyName.ProjectName.Client.Dtos;
using Leistd.Security.Claims;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Validation;
#endif

namespace CompanyName.ProjectName.IntegrationTests;

#if (OpenIddictServer)
public sealed class ServiceInvocationTests(ProjectWebApplicationFactory factory) : IClassFixture<ProjectWebApplicationFactory>
{
    [Fact]
    public async Task Generated_client_explicit_machine_authentication_calls_anonymous_API_and_maps_natural_user_rejection()
    {
        var clientId = $"machine-{Guid.NewGuid():N}";
        const string secret = "ClientPackage!Secret1";
        using (var scope = factory.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = clientId, ClientSecret = secret,
                ClientType = OpenIddictConstants.ClientTypes.Confidential,
                ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                Permissions =
                {
                    OpenIddictConstants.Permissions.Endpoints.Token,
                    OpenIddictConstants.Permissions.GrantTypes.ClientCredentials,
                    OpenIddictConstants.Permissions.Prefixes.Scope + new OAuthResourceOptions().Resource
                }
            });
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Leistd:ServiceAuth:Authority"] = factory.Services.GetRequiredService<IOptions<OAuthServerOptions>>().Value.Issuer ?? "https://localhost/",
            ["Leistd:ServiceAuth:ClientId"] = clientId,
            ["Leistd:ServiceAuth:ClientSecret"] = secret,
            ["Leistd:ServiceClients:MyProject:BaseAddress"] = "https://localhost/",
            ["Leistd:ServiceClients:MyProject:Scope"] = new OAuthResourceOptions().Resource
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(); services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new ProtocolTransport(factory.Server));
        services.AddServiceAuthentication();
        services.AddMyProjectClient().AddClientCredentials()
            .ConfigurePrimaryHttpMessageHandler(() => factory.Server.CreateHandler());
        await using var caller = services.BuildServiceProvider();
        var client = caller.GetRequiredService<IMyProjectClient>();
        var info = await client.GetServiceInfoAsync();
        Assert.False(string.IsNullOrWhiteSpace(info.Service));
        // 403 证明机器令牌已被认证，但不满足自然人策略；未认证调用会返回 401。
        var rejection = await Assert.ThrowsAsync<RemoteServiceException>(() => client.WhoAmIAsync());
        Assert.Equal(403, rejection.RemoteStatusCode);
        Assert.False(string.IsNullOrWhiteSpace(rejection.RemoteTraceId));
    }

    private sealed class ProtocolTransport(TestServer server) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            if (!builder.Name!.StartsWith(typeof(OpenIddictClientSystemNetHttpOptions).Assembly.GetName().Name!, StringComparison.Ordinal)) return;
            builder.PrimaryHandler.Dispose();
            builder.PrimaryHandler = server.CreateHandler();
        };
    }
}
#else
/// <summary>
/// 资源服务作为 Token Exchange 的接收方，同样提供调用诊断端点：经生成的 Client 与真实 Bearer 验签，
/// 交换令牌代表的自然人与调用方客户端原样呈现，机器令牌不满足自然人策略。
/// </summary>
public sealed class ServiceInvocationTests
{
    private const string Issuer = "https://identity.test/";
    private const string Audience = "resource-api";

    [Fact]
    public async Task Generated_client_whoami_presents_the_exchanged_user_and_the_calling_client()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "service-invocation-test" };
        using var factory = new ProjectWebApplicationFactory { UseProductionAuthentication = true };
        using var host = SignedBy(factory, key);
        var subject = Guid.NewGuid();
        await using var caller = Caller(host, Token(key, subject.ToString(), "orders-worker", "dave"));

        var identity = await caller.GetRequiredService<IMyProjectClient>().WhoAmIAsync();

        Assert.Equal(new WhoAmIDto(subject, "dave", "orders-worker"), identity);
    }

    [Fact]
    public async Task Generated_client_whoami_rejects_a_machine_token_by_the_natural_user_policy()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "service-invocation-test" };
        using var factory = new ProjectWebApplicationFactory { UseProductionAuthentication = true };
        using var host = SignedBy(factory, key);

        await using (var anonymous = Caller(host, token: null))
        {
            var unauthenticated = await Assert.ThrowsAsync<RemoteServiceException>(
                () => anonymous.GetRequiredService<IMyProjectClient>().WhoAmIAsync());
            Assert.Equal(401, unauthenticated.RemoteStatusCode);
        }

        // 403 而不是 401：机器令牌已通过验签，只是不代表自然人
        await using var machine = Caller(host, Token(key, ClientSubject.Format("orders-worker"), "orders-worker", null));
        var rejection = await Assert.ThrowsAsync<RemoteServiceException>(
            () => machine.GetRequiredService<IMyProjectClient>().WhoAmIAsync());
        Assert.Equal(403, rejection.RemoteStatusCode);
    }

    // 调用方进程：只注册生成的 Client，经测试宿主的处理器发出请求，按需附上 Bearer
    private static ServiceProvider Caller(WebApplicationFactory<Program> host, string? token)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Leistd:ServiceClients:MyProject:BaseAddress"] = "https://localhost/"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        var client = services.AddMyProjectClient()
            .ConfigurePrimaryHttpMessageHandler(() => host.Server.CreateHandler());
        if (token is not null) client.AddHttpMessageHandler(() => new BearerHandler(token));
        return services.BuildServiceProvider();
    }

    private sealed class BearerHandler(string token) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static WebApplicationFactory<Program> SignedBy(ProjectWebApplicationFactory factory, SecurityKey key) =>
        factory.WithWebHostBuilder(builder => builder.UseSetting("Authentication:Audience", Audience)
            .ConfigureTestServices(services => services.Configure<OpenIddictValidationOptions>(options =>
            {
                options.Configuration = new OpenIddictConfiguration { Issuer = new Uri(Issuer) };
                options.Configuration.SigningKeys.Add(key);
            })));

    private static string Token(SecurityKey key, string subject, string clientId, string? username)
    {
        var claims = new Dictionary<string, object>
        {
            [CustomClaimTypes.Subject] = subject,
            [CustomClaimTypes.ClientId] = clientId,
            ["jti"] = Guid.NewGuid().ToString()
        };
        if (username is not null) claims["preferred_username"] = username;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = Audience, Claims = claims, TokenType = "at+jwt",
            IssuedAt = DateTime.UtcNow, NotBefore = DateTime.UtcNow.AddSeconds(-5), Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        });
    }
}
#endif
#endif
