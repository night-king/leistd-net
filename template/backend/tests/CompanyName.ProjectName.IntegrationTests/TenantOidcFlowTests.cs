#if (OpenIddictServer)
using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Application.OpenApplications.Dtos;
using CompanyName.ProjectName.Application.Auth.Options;
using Leistd.MultiTenancy.AspNetCore.Options;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Dtos;
using Leistd.MultiTenancy.Stores;
using Leistd.Security.Claims;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>租户用户走完整个 OIDC 流程：授权、授权码换令牌、userinfo、刷新令牌。</summary>
/// <remarks>
/// 令牌端点与 userinfo 的请求不带用户会话，请求本身只能解析出宿主；用户与租户必须取自令牌主体，
/// 在其租户内加载用户。取错上下文时租户用户在宿主分区里查不到，只在租户用户身上失败——宿主用户的用例照样绿。
/// </remarks>
public sealed class TenantOidcFlowTests(ProjectWebApplicationFactory factory)
    : AuthorizationTestBase(factory), IClassFixture<ProjectWebApplicationFactory>
{
    private const string TenantAdminPassword = "Tenant@123456";

    // 调用本服务 API 的令牌必须申请它的 scope：受众由授予的 scope 推出，API 只接受受众是自己的令牌
    private static readonly string ApiScope = new OAuthResourceOptions().Resource;

    private const string DownstreamApi = "orders-api";

    /// <summary>
    /// 为下游 API 签发令牌：配置里登记的下游 API 可以授予给客户端，申请它得到的令牌受众就是它，
    /// 而本服务自己的 API 不接受这样的令牌。
    /// </summary>
    /// <remarks>受众写死成本服务时，下游拿不到属于自己的令牌；不校验受众时，签给下游的令牌又能调用本服务。</remarks>
    [Fact]
    public async Task A_downstream_api_scope_yields_a_token_for_that_api_only()
    {
        using var host = Factory.WithWebHostBuilder(builder =>
            builder.UseSetting($"{OAuthResourceOptions.SectionName}:ApiResources:0:Name", DownstreamApi));

        using var hostAdmin = await ProjectWebApplicationFactory.LoginAsync(
            host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var scopes = await hostAdmin.Client.GetFromJsonAsync<OpenApplicationScopeOutputDto[]>("/api/v1/open-applications/scopes");
        Assert.Contains(scopes!, scope => scope.Name == DownstreamApi && !scope.MachineOnly);
        Assert.Contains(scopes!, scope => scope.Name == ApiScope && !scope.MachineOnly);

        var (clientId, clientSecret) = await CreateClientAsync(hostAdmin.Client, DownstreamApi);
        var tokens = await AuthorizeAndExchangeAsync(host, hostAdmin, clientId, clientSecret, DownstreamApi);

        Assert.Equal([DownstreamApi], ReadAudiences(tokens.AccessToken));

        using var api = CreateHttpsClient(host);
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/v1/auth/me")).StatusCode);
        // userinfo 属于授权服务器本身，不看受众
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/connect/userinfo")).StatusCode);
    }

    // 目录是唯一来源：从 OAuth:ApiResources 移除的下游 API，重启后它的 scope 从表里删掉，不能再被申请到
    [Fact]
    public async Task A_removed_api_resource_disappears_from_the_scope_table()
    {
        using (var configured = Factory.WithWebHostBuilder(builder =>
                   builder.UseSetting($"{OAuthResourceOptions.SectionName}:ApiResources:0:Name", DownstreamApi)))
        {
            Assert.True(await ScopeExistsAsync(configured, DownstreamApi));
        }

        using var removed = Factory.WithWebHostBuilder(builder =>
            builder.UseSetting($"{OAuthResourceOptions.SectionName}:ApiResources:0:Name", "other-api"));

        Assert.False(await ScopeExistsAsync(removed, DownstreamApi));
        Assert.True(await ScopeExistsAsync(removed, "other-api"));
    }

    private static async Task<bool> ScopeExistsAsync(WebApplicationFactory<Program> host, string name)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var scopes = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();
        return await scopes.FindByNameAsync(name) is not null;
    }

    // 下游 API 的标识会登记为同名 scope：为空、重复或撞上内置 scope 时启动失败，报出键名
    [Theory]
    [InlineData("openid", null)]
    [InlineData(DownstreamApi, DownstreamApi)]
    [InlineData(" ", null)]
    public void Conflicting_api_resources_fail_startup(string first, string? second)
    {
        using var host = Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting($"{OAuthResourceOptions.SectionName}:ApiResources:0:Name", first);
            if (second is not null)
            {
                builder.UseSetting($"{OAuthResourceOptions.SectionName}:ApiResources:1:Name", second);
            }
        });

        var exception = Assert.ThrowsAny<Exception>(() => host.Services);

        Assert.Contains("OAuth:ApiResources", exception.ToString());
    }

    [Theory]
    [InlineData("orders-api", "orders-api")]
    [InlineData("https://api.example.test/orders", "orders-worker")]
    public async Task Only_the_resource_owner_can_exchange_a_token_from_another_presenter(string resource, string owner)
    {
        using var host = Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OAuth:ApiResources:0:Name", resource);
            builder.UseSetting("OAuth:ApiResources:0:Scope", "orders.read");
            builder.UseSetting("OAuth:ApiResources:0:OwnerClientId", owner);
            builder.UseSetting("OAuth:ApiResources:1:Name", "https://api.example.test/inventory");
            builder.UseSetting("OAuth:ApiResources:1:Scope", "inventory.read");
            builder.UseSetting("OAuth:ApiResources:1:OwnerClientId", owner);
            builder.UseSetting("OAuth:ApiResources:2:Name", "https://api.example.test/billing");
            builder.UseSetting("OAuth:ApiResources:2:Scope", "billing.read");
        });
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var presenter = await CreateClientAsync(admin.Client, "orders.read", "inventory.read");
        var authorized = await CreateExchangeClientAsync(admin.Client, owner);
        var unauthorized = await CreateExchangeClientAsync(admin.Client, "another-client-" + Guid.NewGuid().ToString("N"));
        foreach (var sourceScope in new[] { "orders.read", "inventory.read" })
        {
            var source = await AuthorizeAndExchangeAsync(host, admin, presenter.ClientId, presenter.ClientSecret, sourceScope);
            using var client = CreateHttpsClient(host);
            async Task<HttpResponseMessage> ExchangeAsync((string Id, string Secret) caller) => await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
                ["client_id"] = caller.Id, ["client_secret"] = caller.Secret,
                ["subject_token"] = source.AccessToken,
                ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
                ["requested_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
                ["audience"] = "https://api.example.test/billing", ["scope"] = "billing.read"
            }));
            using var accepted = await ExchangeAsync(authorized);
            Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());
            var token = await accepted.Content.ReadFromJsonAsync<TokenResponse>();
            Assert.Equal(["https://api.example.test/billing"], ReadAudiences(token!.AccessToken));
            // 默认寿命：源令牌 10 分钟，交换令牌取 120 秒上限（源令牌剩余寿命更长）
            Assert.Equal(600, Lifetime(source.AccessToken));
            Assert.Equal(120, Lifetime(token.AccessToken));
            using var rejected = await ExchangeAsync(unauthorized);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
    }

    /// <summary>配置的访问令牌寿命落到实际签发的令牌上；交换令牌不长于源令牌，源令牌比 120 秒上限先到期时取源令牌的到期时刻。</summary>
    [Fact]
    public async Task Configured_access_token_lifetime_applies_and_bounds_exchanged_tokens()
    {
        using var host = Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OAuth:AccessTokenLifetime", "00:01:30");
            builder.UseSetting("OAuth:ApiResources:0:Name", "lifetime-probe-api");
            builder.UseSetting("OAuth:ApiResources:0:Scope", "lifetime-probe.read");
            builder.UseSetting("OAuth:ApiResources:1:Name", "https://api.example.test/billing");
            builder.UseSetting("OAuth:ApiResources:1:Scope", "billing.read");
        });
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var presenter = await CreateClientAsync(admin.Client, "lifetime-probe.read");
        var exchanger = await CreateExchangeClientAsync(admin.Client, "lifetime-probe-api");
        var source = await AuthorizeAndExchangeAsync(host, admin, presenter.ClientId, presenter.ClientSecret, "lifetime-probe.read");
        Assert.Equal(90, Lifetime(source.AccessToken));

        using var client = CreateHttpsClient(host);
        using var exchanged = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["client_id"] = exchanger.Id, ["client_secret"] = exchanger.Secret,
            ["subject_token"] = source.AccessToken,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["requested_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["audience"] = "https://api.example.test/billing", ["scope"] = "billing.read"
        }));
        Assert.True(exchanged.IsSuccessStatusCode, await exchanged.Content.ReadAsStringAsync());
        var token = await exchanged.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.Equal(ReadClaim(source.AccessToken, "exp"), ReadClaim(token!.AccessToken, "exp"));
    }

    private static async Task<(string Id, string Secret)> CreateExchangeClientAsync(HttpClient admin, string id)
    {
        using var created = await admin.PostAsJsonAsync("/api/v1/open-applications", new
        {
            sessionBound = false,
            clientId = id, displayName = id, applicationType = "service", clientType = "confidential",
            permissions = new[] { "ept:token", "gt:urn:ietf:params:oauth:grant-type:token-exchange", "scp:billing.read", "aud:https://api.example.test/billing" },
            requirements = Array.Empty<string>(), redirectUris = Array.Empty<string>(), postLogoutRedirectUris = Array.Empty<string>()
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var app = await created.Content.ReadFromJsonAsync<OpenApplicationOutputDto>();
        using var reset = await admin.PostAsync($"/api/v1/open-applications/{app!.Id}/reset-secret", null);
        var secret = await reset.Content.ReadFromJsonAsync<ResetOpenApplicationSecretOutputDto>();
        return (id, secret!.ClientSecret);
    }

    [Fact]
    public void One_resource_cannot_have_two_owners_even_with_different_scope_names()
    {
        using var host = Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OAuth:ApiResources:0:Name", "https://api.example.test/owned");
            builder.UseSetting("OAuth:ApiResources:0:Scope", "first.read");
            builder.UseSetting("OAuth:ApiResources:0:OwnerClientId", "first-client");
            builder.UseSetting("OAuth:ApiResources:1:Name", "https://api.example.test/owned");
            builder.UseSetting("OAuth:ApiResources:1:Scope", "second.read");
            builder.UseSetting("OAuth:ApiResources:1:OwnerClientId", "second-client");
        });
        Assert.Contains("OAuth:ApiResources", Assert.ThrowsAny<Exception>(() => host.Services).ToString());
    }

    private static long Lifetime(string accessToken) => ReadClaim(accessToken, "exp") - ReadClaim(accessToken, "iat");

    private static long ReadClaim(string accessToken, string name)
    {
        using var json = JsonDocument.Parse(Base64Url.DecodeFromChars(accessToken.Split('.')[1]));
        return json.RootElement.GetProperty(name).GetInt64();
    }

    private static string[] ReadAudiences(string accessToken)
    {
        var payload = accessToken.Split('.')[1];
        using var json = JsonDocument.Parse(Base64Url.DecodeFromChars(payload));
        return json.RootElement.GetProperty("aud") switch
        {
            { ValueKind: JsonValueKind.String } single => [single.GetString()!],
            var many => many.EnumerateArray().Select(item => item.GetString()!).ToArray()
        };
    }

    [Fact]
    public Task A_tenant_user_can_exchange_a_code_read_userinfo_and_refresh() => RunTenantFlowAsync(Factory);

    /// <summary>宿主把主体标识换成别的 claim 名：签发、换码、userinfo、资源服务判定自然人与机器主体都按它走。</summary>
    /// <remarks>令牌只签 sub 的话，换码按配置读不到用户、资源服务判不出自然人，链路在签发方自己这里就断了。</remarks>
    [Fact]
    public async Task A_configured_user_id_claim_carries_through_the_whole_flow()
    {
        using var host = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<ClaimTypeOptions>(options => options.UserIds = ["user_id"])));

        await RunTenantFlowAsync(host);

        using var hostAdmin = await ProjectWebApplicationFactory.LoginAsync(
            host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var machine = await CreateMachineClientAsync(host, hostAdmin.Client, "tenant-routing.read");
        var databases = await machine.GetAsync("/api/v1/tenant-connections/databases?name=Default");
        Assert.NotEqual(HttpStatusCode.Unauthorized, databases.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, databases.StatusCode);
    }

    /// <summary>迁移清单的线上形状：健康租户的连接与取不出连接的租户一起下发，只有迁移身份读得到。</summary>
    /// <remarks>
    /// 失败租户若在线上丢了，Resource 的迁移作业会以为一切正常，那个库就停在旧结构上；
    /// 只持有读路由权限的常驻服务则绝不能拿到这份带全部明文连接串的清单。
    /// </remarks>
    [Fact]
    public async Task The_migration_list_carries_failed_tenants_and_only_the_migration_identity_reads_it()
    {
        // 独立宿主：登记的连接会进入同库其他用例的逐库清单
        using var isolated = new ProjectWebApplicationFactory();
        Guid healthy, broken;
        await using (var scope = isolated.Services.CreateAsyncScope())
        {
            var tenants = scope.ServiceProvider.GetRequiredService<ITenantManager>();
            var connections = scope.ServiceProvider.GetRequiredService<ITenantConnectionConfigurationManager>();
            healthy = (await tenants.CreateAsync("migration-healthy", null, isActive: false)).Id;
            broken = (await tenants.CreateAsync("migration-broken", null, isActive: false)).Id;
            await connections.SetAsync(healthy, "default", "Host=healthy-db;Password=healthy-secret", expectedVersion: null);
            // 只登记了别的服务的连接名：本服务的名字解析不出
            await connections.SetAsync(broken, "billing", "Host=billing-db;Password=billing-secret", expectedVersion: null);
        }

        using var hostAdmin = await ProjectWebApplicationFactory.LoginAsync(
            isolated, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var migration = await CreateMachineClientAsync(isolated, hostAdmin.Client, "tenant-migration.read");
        using var response = await migration.GetAsync("/api/v1/tenant-connections/migration?name=Default");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var list = await response.Content.ReadFromJsonAsync<TenantMigrationConnectionListOutputDto>();
        Assert.NotNull(list);
        Assert.Equal(healthy, Assert.Single(list.Connections).TenantId);
        var failure = Assert.Single(list.FailedTenants);
        Assert.Equal(broken, failure.TenantId);
        Assert.DoesNotContain("billing-secret", failure.Reason);

        using var routing = await CreateMachineClientAsync(isolated, hostAdmin.Client, "tenant-routing.read");
        using var denied = await routing.GetAsync("/api/v1/tenant-connections/migration?name=Default");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    private async Task RunTenantFlowAsync(WebApplicationFactory<Program> host)
    {
        using var hostAdmin = await ProjectWebApplicationFactory.LoginAsync(
            host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var tenantId = await CreateTenantAsync(hostAdmin.Client, $"oidc{Guid.CreateVersion7():N}"[..16]);
        var (clientId, clientSecret) = await CreateClientAsync(hostAdmin.Client, ApiScope);
        using var tenantSession = await LoginTenantAdminAsync(host, tenantId);

        var tokens = await AuthorizeAndExchangeAsync(host, tenantSession, clientId, clientSecret, ApiScope);

        using var api = CreateHttpsClient(host);
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var userInfo = await api.GetAsync("/connect/userinfo");
        Assert.Equal(HttpStatusCode.OK, userInfo.StatusCode);
        using (var body = JsonDocument.Parse(await userInfo.Content.ReadAsStringAsync()))
        {
            Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("sub").GetString()));
        }

        // 资源服务按默认策略判定自然人
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/v1/auth/me")).StatusCode);

        Assert.False(string.IsNullOrEmpty(tokens.RefreshToken));
        using var refresh = CreateHttpsClient(host);
        var refreshed = await refresh.PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("refresh_token", tokens.RefreshToken!),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret)
        ]));
        Assert.True(refreshed.IsSuccessStatusCode, await refreshed.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> CreateTenantAsync(HttpClient hostAdmin, string name)
    {
        var response = await hostAdmin.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = name,
            DisplayName = $"{name} Inc.",
            AdminEmail = $"admin@{name}.example.com",
            AdminPassword = TenantAdminPassword
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<AuthenticatedSession> LoginTenantAdminAsync(WebApplicationFactory<Program> host, Guid tenantId)
    {
        var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        client.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, tenantId.ToString());
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/session-login",
            new { UsernameOrEmail = "admin", Password = TenantAdminPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cookie = string.Join("; ", response.Headers.GetValues("Set-Cookie")
            .Select(value => value.Split(';', 2)[0]));
        return new AuthenticatedSession(client, cookie);
    }
}
#endif
