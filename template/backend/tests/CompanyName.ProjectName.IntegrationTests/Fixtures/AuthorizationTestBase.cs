#if (LocalIdentity)
using System.Net;
#if (OpenIddictServer)
using System.Net.Http.Headers;
#endif
using System.Net.Http.Json;
#if (OpenIddictServer)
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CompanyName.ProjectName.Application.OpenApplications.Dtos;
#endif
using CompanyName.ProjectName.Application.Users.Dtos;
#if (OpenIddictServer)
using Microsoft.AspNetCore.Mvc.Testing;
#endif
using Microsoft.Extensions.DependencyInjection;
using Leistd.Authorization.Grants;

namespace CompanyName.ProjectName.IntegrationTests.Fixtures;

/// <summary>授权类集成测试的共享装配：建用户、授予权限，以及两处共用的常量。</summary>
/// <remarks>
/// <para>提到基类是因为这些装配被<b>两批剪裁归属不同</b>的用例共用：
/// <c>AuthorizationAndAuditingTests</c> 覆盖权限、审计、撤权等横切主题，只要有本地身份就成立；
/// 另一批（仅在签发令牌的形态下存在）需要一个能签发令牌的授权服务器。</para>
/// <para>两批必须分文件（否则关掉 OIDC 时没法只剪掉后者），但装配相同——
/// 复制一份会让"改一处忘另一处"成为常态。</para>
/// </remarks>
public abstract class AuthorizationTestBase(ProjectWebApplicationFactory factory)
{
    /// <summary>
    /// 测试用户口令。必须满足服务端 <c>PasswordPolicy</c>：不满足时失败会出现在
    /// "创建测试用户"这种与被测行为无关的地方，很难一眼看出根因
    /// </summary>
    protected const string TestPassword = "IntegrationTests!User";

    /// <summary>授权码流程的回调地址。必须是 https——OIDC 端点只收 https</summary>
    protected const string RedirectUri = "https://localhost/callback";

    /// <summary>供派生类访问宿主工厂</summary>
    protected ProjectWebApplicationFactory Factory { get; } = factory;

    /// <summary>直接经管理器授予权限，绕过 HTTP 层——用于铺设前置状态，不作为被测路径</summary>
    protected async Task GrantAsync(string providerName, Guid providerKey, params string[] permissions)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IPermissionGrantManager>();
        foreach (var permission in permissions)
        {
            await manager.GrantAsync(permission, providerName, providerKey.ToString());
        }
    }

    /// <summary>经 API 建一个测试用户。失败时把状态码与响应体一起报出，避免只看到断言假</summary>
    protected static async Task<UserManagementOutputDto> CreateUserAsync(
        HttpClient client,
        List<Guid>? roleIds = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var response = await client.PostAsJsonAsync(
            "/api/v1/users",
            new CreateUserInputDto
            {
                Username = $"user_{suffix}",
                Email = $"user_{suffix}@example.test",
                DisplayName = "Integration user",
                Password = TestPassword,
                IsActive = true,
                RoleIds = roleIds ?? []
            });

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Create user failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<UserManagementOutputDto>())!;
    }
#if (OpenIddictServer)

    /// <summary>登记一个只允许 client_credentials 的机器客户端，换取带 <paramref name="scope"/> 的令牌，返回已带 Bearer 的 HTTPS 客户端</summary>
    protected static async Task<HttpClient> CreateMachineClientAsync(
        WebApplicationFactory<Program> host, HttpClient hostAdmin, string scope)
    {
        var clientId = $"client-{Guid.CreateVersion7():N}";
        var created = await hostAdmin.PostAsJsonAsync("/api/v1/open-applications", new
        {
            sessionBound = false,
            clientId,
            displayName = "Workload",
            applicationType = "service",
            clientType = "confidential",
            permissions = new[] { "ept:token", "gt:client_credentials", $"scp:{scope}" },
            requirements = Array.Empty<string>(),
            redirectUris = Array.Empty<string>(),
            postLogoutRedirectUris = Array.Empty<string>()
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var application = await created.Content.ReadFromJsonAsync<OpenApplicationOutputDto>();
        Assert.NotNull(application);
        var reset = await hostAdmin.PostAsync($"/api/v1/open-applications/{application.Id}/reset-secret", null);
        var secret = await reset.Content.ReadFromJsonAsync<ResetOpenApplicationSecretOutputDto>();
        Assert.NotNull(secret);

        var machine = CreateHttpsClient(host);
        var token = await machine.PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", secret.ClientSecret),
            new KeyValuePair<string, string>("scope", scope)
        ]));
        Assert.True(token.IsSuccessStatusCode, await token.Content.ReadAsStringAsync());
        var payload = await token.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(payload);
        machine.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", payload.AccessToken);
        return machine;
    }

    /// <summary>登记一个走授权码流程的 Web 机密客户端，可申请 openid、offline_access 与 <paramref name="extraScopes"/></summary>
    protected static async Task<(string ClientId, string ClientSecret)> CreateClientAsync(
        HttpClient hostAdmin, params string[] extraScopes)
    {
        var clientId = $"client-{Guid.CreateVersion7():N}";
        var created = await hostAdmin.PostAsJsonAsync("/api/v1/open-applications", new
        {
            sessionBound = false,
            clientId,
            displayName = "Tenant OIDC probe",
            applicationType = "web",
            clientType = "confidential",
            permissions = (string[])
            [
                "ept:authorization", "ept:token", "gt:authorization_code", "gt:refresh_token", "rst:code",
                "scp:openid", "scp:offline_access", .. extraScopes.Select(scope => $"scp:{scope}")
            ],
            requirements = Array.Empty<string>(),
            redirectUris = new[] { RedirectUri },
            postLogoutRedirectUris = Array.Empty<string>()
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var application = await created.Content.ReadFromJsonAsync<OpenApplicationOutputDto>();
        Assert.NotNull(application);

        var reset = await hostAdmin.PostAsync($"/api/v1/open-applications/{application.Id}/reset-secret", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var secret = await reset.Content.ReadFromJsonAsync<ResetOpenApplicationSecretOutputDto>();
        Assert.NotNull(secret);
        return (clientId, secret.ClientSecret);
    }

    // 授权端点凭会话 Cookie 直接签发授权码（无同意页）；PKCE 必须是真的 S256
    protected static async Task<TokenResponse> AuthorizeAndExchangeAsync(
        WebApplicationFactory<Program> host, AuthenticatedSession session, string clientId, string clientSecret, string apiScope)
    {
        var verifier = Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N");
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        using var browser = CreateHttpsClient(host);
        browser.DefaultRequestHeaders.Add("Cookie", session.Cookie);
        var authorize = await browser.GetCachedAsync(
            "/connect/authorize" +
            $"?client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&response_type=code&scope=openid%20offline_access%20{Uri.EscapeDataString(apiScope)}" +
            $"&code_challenge={challenge}&code_challenge_method=S256");
        Assert.Equal(HttpStatusCode.Found, authorize.StatusCode);

        var code = authorize.Headers.Location!.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0] == "code")
            .Select(pair => Uri.UnescapeDataString(pair[1]))
            .Single();

        using var exchange = CreateHttpsClient(host);
        var token = await exchange.PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new KeyValuePair<string, string>("code", code),
            new KeyValuePair<string, string>("redirect_uri", RedirectUri),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret),
            new KeyValuePair<string, string>("code_verifier", verifier)
        ]));
        Assert.True(token.IsSuccessStatusCode, await token.Content.ReadAsStringAsync());

        var payload = await token.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(payload);
        return payload;
    }

    // OpenIddict 的授权与令牌端点只收 HTTPS；改基地址即可让 Request.IsHttps 成立
    protected static HttpClient CreateHttpsClient(WebApplicationFactory<Program> host)
    {
        var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        client.BaseAddress = new Uri("https://localhost");
        return client;
    }

    protected sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken);
#endif
}
#endif
