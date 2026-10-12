using System.Net;
#if (OpenIddictServer)
using System.Net.Http.Headers;
#endif
#if (RemoteTokenAuth)
using System.Security.Cryptography;
#endif
using CompanyName.ProjectName.Application.Permissions.Provider;
#if (OpenIddictServer && IncludeMultiTenancy)
using CompanyName.ProjectName.Application.Auth.Options;
using CompanyName.ProjectName.Application.TenantConnections.Constants;
#endif
using Leistd.Authorization.Constants;
#if (RemoteTokenAuth)
using Leistd.Authorization.Grants;
using Leistd.MultiTenancy.Context;
using Leistd.Security.Claims;
using Microsoft.AspNetCore.TestHost;
#endif
#if (LocalIdentity && IncludeMultiTenancy)
using Leistd.MultiTenancy.Stores;
#endif
#if (IncludeMultiTenancy || RemoteTokenAuth)
using Microsoft.Extensions.DependencyInjection;
#endif
#if (RemoteTokenAuth)
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Validation;
#endif

namespace CompanyName.ProjectName.IntegrationTests;
#if (LocalIdentity)

/// <summary>组件端点在生产认证组合下的 401/403/成功分界。</summary>
/// <remarks>
/// <para>组件的具名策略不指定认证方案，匿名被拒时由宿主默认挑战方案回应。签发形态的默认挑战若交给 OpenIddict，
/// 它对没经它认证的请求答 insufficient_access，纯权限端点匿名就成了 403，而叠了 CurrentUser（自带方案）的端点照常 401。</para>
/// <para>按端点的真实策略分三类断言：纯权限端点要有权成功对照（只测拒绝发现不了"全部拒绝"），
/// 自用端点普通成员即可访问，机器端点只认 Bearer。</para>
/// </remarks>
public sealed class ComponentEndpointAuthenticationTests(ProjectWebApplicationFactory factory)
    : AuthorizationTestBase(factory), IClassFixture<ProjectWebApplicationFactory>
{
    private static readonly string[] SelfServiceEndpoints =
    [
        "/api/v1/settings",
        "/api/v1/permissions/current",
#if (IncludeNotifications)
        "/api/v1/notifications",
        "/api/v1/notifications/unread-count",
#endif
    ];

    [Fact]
    public async Task Permission_endpoints_answer_anonymous_401_unpermitted_403_and_permitted_success()
    {
        using var admin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var endpoints = new List<string>
        {
            "/api/v1/permissions/definitions",
#if (IncludeOperationRecords)
            "/api/v1/operation-records?offset=0&limit=10",
            "/api/v1/operation-records/filter-options",
            "/api/v1/operation-records/export",
#endif
#if (IncludeMultiTenancy)
            "/api/v1/tenants?offset=0&limit=10",
            $"/api/v1/tenant-connections/{await CreateTenantAsync()}",
#endif
        };
        var member = await CreateUserAsync(admin.Client);
        using var memberSession = await Factory.LoginAsync(member.Username, TestPassword);
        using var anonymous = Factory.CreateProjectClient();

        foreach (var path in endpoints)
        {
            using var rejected = await AssertStatusAsync(anonymous, path, HttpStatusCode.Unauthorized);
            // 无凭据由会话 Cookie 回应：不带 Bearer 的 insufficient_access，也不宣告 Bearer challenge（取舍见认证规范）
            Assert.Empty(rejected.Headers.WwwAuthenticate);
            (await AssertStatusAsync(memberSession.Client, path, HttpStatusCode.Forbidden)).Dispose();
        }

        string[] permissions =
        [
            PermissionConstant.Roles.ManagePermissions,
#if (IncludeOperationRecords)
            PermissionConstant.OperationRecords.Default,
            PermissionConstant.OperationRecords.Export,
#endif
#if (IncludeMultiTenancy)
            PermissionConstant.Tenants.Default,
            PermissionConstant.Tenants.Update,
#endif
        ];
        await GrantAsync(PermissionGrantProviderNames.User, member.Id, permissions);

        foreach (var path in endpoints)
        {
            (await AssertStatusAsync(memberSession.Client, path, HttpStatusCode.OK)).Dispose();
        }
    }

    [Fact]
    public async Task Self_service_endpoints_answer_anonymous_401_and_admit_an_ordinary_member()
    {
        using var admin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var member = await CreateUserAsync(admin.Client);
        using var memberSession = await Factory.LoginAsync(member.Username, TestPassword);
        using var anonymous = Factory.CreateProjectClient();

        foreach (var path in SelfServiceEndpoints)
        {
            (await AssertStatusAsync(anonymous, path, HttpStatusCode.Unauthorized)).Dispose();
            (await AssertStatusAsync(memberSession.Client, path, HttpStatusCode.OK)).Dispose();
        }
    }
#if (OpenIddictServer)

    /// <summary>带 Authorization 头只选 Bearer：令牌无效时 401，不回退到同一请求里有效的会话 Cookie。</summary>
    [Fact]
    public async Task An_invalid_bearer_is_not_rescued_by_a_valid_session_cookie()
    {
        using var admin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        foreach (var path in (string[])["/api/v1/settings", "/api/v1/permissions/definitions"])
        {
            (await AssertStatusAsync(admin.Client, path, HttpStatusCode.OK)).Dispose();

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
            using var response = await admin.Client.SendAsync(request);
            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"GET {path} with a valid cookie and an invalid bearer returned {(int)response.StatusCode}, expected 401");
            Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
        }
    }
#if (IncludeMultiTenancy)

    /// <summary>机器端点的策略自带 Bearer 方案：匿名得到标准 Bearer challenge，管理员会话、自然人令牌与错误 scope 都进不去。</summary>
    /// <remarks>机器 scope 只发给 client_credentials 客户端，自然人令牌拿不到它；这里的自然人令牌带本服务 API scope，验签通过、主体不对。</remarks>
    [Fact]
    public async Task Machine_endpoints_admit_only_a_bearer_with_the_matching_scope()
    {
        const string runtimePath = "/api/v1/tenant-connections/databases?name=Default&activeOnly=true";
        const string migrationPath = "/api/v1/tenant-connections/migration?name=Default";
        using var admin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var anonymous = Factory.CreateProjectClient();
        using var routing = await CreateMachineClientAsync(Factory, admin.Client, TenantConnectionScopes.RuntimeRead);
        using var migration = await CreateMachineClientAsync(Factory, admin.Client, TenantConnectionScopes.MigrationRead);
        using var person = await CreateNaturalPersonBearerClientAsync(admin.Client);

        foreach (var path in (string[])[runtimePath, migrationPath])
        {
            using var rejected = await AssertStatusAsync(anonymous, path, HttpStatusCode.Unauthorized);
            Assert.Contains(rejected.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
            (await AssertStatusAsync(admin.Client, path, HttpStatusCode.Unauthorized)).Dispose();
            (await AssertStatusAsync(person, path, HttpStatusCode.Forbidden)).Dispose();
        }

        (await AssertStatusAsync(routing, runtimePath, HttpStatusCode.OK)).Dispose();
        (await AssertStatusAsync(migration, runtimePath, HttpStatusCode.Forbidden)).Dispose();
        (await AssertStatusAsync(migration, migrationPath, HttpStatusCode.OK)).Dispose();
        (await AssertStatusAsync(routing, migrationPath, HttpStatusCode.Forbidden)).Dispose();
    }

    // 普通成员经授权码流程拿到的有效访问令牌
    private async Task<HttpClient> CreateNaturalPersonBearerClientAsync(HttpClient admin)
    {
        var apiScope = new OAuthResourceOptions().Resource;
        var member = await CreateUserAsync(admin);
        using var memberSession = await Factory.LoginAsync(member.Username, TestPassword);
        var (clientId, clientSecret) = await CreateClientAsync(admin, apiScope);
        var tokens = await AuthorizeAndExchangeAsync(Factory, memberSession, clientId, clientSecret, apiScope);

        var client = CreateHttpsClient(Factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        (await AssertStatusAsync(client, "/api/v1/auth/me", HttpStatusCode.OK)).Dispose();
        return client;
    }
#endif
#endif
#if (IncludeMultiTenancy)

    // 直接经管理器建一个停用租户，只为让租户连接端点有可读的对象；不走租户开通的全流程
    private async Task<Guid> CreateTenantAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var tenants = scope.ServiceProvider.GetRequiredService<ITenantManager>();
        return (await tenants.CreateAsync($"auth{Guid.CreateVersion7():N}"[..16], null, isActive: false)).Id;
    }
#endif

    private static async Task<HttpResponseMessage> AssertStatusAsync(HttpClient client, string path, HttpStatusCode expected)
    {
        var response = await client.GetAsync(path);
        Assert.True(
            response.StatusCode == expected,
            $"GET {path} returned {(int)response.StatusCode}, expected {(int)expected}: {await response.Content.ReadAsStringAsync()}");
        return response;
    }
}
#else

/// <summary>组件端点在生产 Bearer 验证下的 401/403/成功分界（Bearer 与 Cookie 并存时的分流见 ResourceBrowserSessionTests）。</summary>
/// <remarks>默认工厂把认证换成替身方案，这里的结论只能取自生产认证组合。</remarks>
public sealed class ComponentEndpointAuthenticationTests
{
    private const string Issuer = "https://identity.test/";
    private const string Audience = "resource-api";

    [Fact]
    public async Task Component_endpoints_answer_anonymous_401_unpermitted_403_and_admit_permitted_members()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "component-endpoint-test" };
        using var production = new ProjectWebApplicationFactory { UseProductionAuthentication = true };
        await using var host = production.WithWebHostBuilder(builder => builder.UseSetting("Authentication:Audience", Audience)
            .ConfigureTestServices(services => services.Configure<OpenIddictValidationOptions>(options =>
            {
                options.Configuration = new OpenIddictConfiguration { Issuer = new Uri(Issuer) };
                options.Configuration.SigningKeys.Add(key);
            })));
        string[] selfService =
        [
            "/api/v1/settings",
            "/api/v1/permissions/current",
#if (IncludeNotifications)
            "/api/v1/notifications",
#endif
        ];
        string[] permissionGated =
        [
            "/api/v1/permissions/definitions",
#if (IncludeOperationRecords)
            "/api/v1/operation-records?offset=0&limit=10",
            "/api/v1/operation-records/filter-options",
#endif
        ];
        var subjectId = Guid.CreateVersion7();
        var tenantId = ProjectWebApplicationFactory.NewTenantId();
        using var anonymous = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var member = ProjectWebApplicationFactory.CreateProjectClient(host);
        member.DefaultRequestHeaders.Authorization = new("Bearer", Token(key, subjectId.ToString(), tenantId));

        foreach (var path in selfService.Concat(permissionGated))
        {
            await AssertStatusAsync(anonymous, path, HttpStatusCode.Unauthorized);
        }

        foreach (var path in selfService)
        {
            await AssertStatusAsync(member, path, HttpStatusCode.OK);
        }

        foreach (var path in permissionGated)
        {
            await AssertStatusAsync(member, path, HttpStatusCode.Forbidden);
        }

        await using (var scope = host.Services.CreateAsyncScope())
        using (scope.ServiceProvider.GetRequiredService<ICurrentTenant>().Change(tenantId))
        {
            var grants = scope.ServiceProvider.GetRequiredService<IPermissionGrantManager>();
            await grants.GrantAsync(PermissionConstant.Roles.ManagePermissions, PermissionGrantProviderNames.User, subjectId.ToString());
#if (IncludeOperationRecords)
            await grants.GrantAsync(PermissionConstant.OperationRecords.Default, PermissionGrantProviderNames.User, subjectId.ToString());
#endif
        }

        foreach (var path in permissionGated)
        {
            await AssertStatusAsync(member, path, HttpStatusCode.OK);
        }
    }

    private static async Task AssertStatusAsync(HttpClient client, string path, HttpStatusCode expected)
    {
        using var response = await client.GetAsync(path);
        Assert.True(
            response.StatusCode == expected,
            $"GET {path} returned {(int)response.StatusCode}, expected {(int)expected}: {await response.Content.ReadAsStringAsync()}");
    }

    private static string Token(SecurityKey key, string subject, Guid? tenantId)
    {
        var claims = new Dictionary<string, object> { [CustomClaimTypes.Subject] = subject, ["jti"] = Guid.NewGuid().ToString() };
        if (tenantId is { } tenant) claims[CustomClaimTypes.TenantId] = tenant.ToString();
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = Audience, Claims = claims, TokenType = "at+jwt",
            IssuedAt = DateTime.UtcNow, NotBefore = DateTime.UtcNow.AddSeconds(-5), Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        });
    }
}
#endif
