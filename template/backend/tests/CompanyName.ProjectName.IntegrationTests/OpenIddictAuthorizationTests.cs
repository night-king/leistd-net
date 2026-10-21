#if (OpenIddictServer)
using Leistd.Authorization.Constants;
using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using CompanyName.ProjectName.Application.OpenApplications.Dtos;
using CompanyName.ProjectName.Application.Auth.Options;
using CompanyName.ProjectName.Application.Permissions.Provider;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>依赖 OIDC 令牌流程的授权用例：客户端凭据、授权码、Bearer 挑战、开放应用端点。</summary>
/// <remarks>
/// <para>与 <c>AuthorizationAndAuditingTests</c> 分开是因为剪裁归属不同：那一批覆盖权限、
/// 审计、撤权等横切主题，只要有本地身份就成立；本批需要一个能签发令牌的授权服务器。
/// 两者混在一个文件里时，关掉 OIDC 就没法只剪掉后者。</para>
/// <para>三个辅助方法（机器客户端、授权码换令牌、HTTPS 客户端）也在这里——它们是这批用例
/// 专有的装配，不被上面那批使用。</para>
/// </remarks>
public sealed class OpenIddictAuthorizationTests(ProjectWebApplicationFactory factory)
    : AuthorizationTestBase(factory), IClassFixture<ProjectWebApplicationFactory>
{
    // 调用本服务 API 的令牌必须申请它的 scope：受众由授予的 scope 推出，API 只接受受众是自己的令牌
    private static readonly string ApiScope = new OAuthResourceOptions().Resource;

    [Fact]
    public async Task Creating_an_application_rejects_scopes_this_server_does_not_register()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        // 客户端能请求一个服务端根本没注册的 scope 时，配置存得下但发令牌时必然被拒——
        // 界面裁掉了选项不代表接口就该收下，写入时报错才不会留一个"配置得上、用不了"的客户端。
        var response = await superAdmin.Client.PostAsJsonAsync(
            "/api/v1/open-applications",
            new
            {
                sessionBound = false,
                clientId = $"client-{Guid.CreateVersion7():N}",
                displayName = "Probe",
                applicationType = "web",
                clientType = "confidential",
                permissions = new[] { "scp:openid", "scp:not_registered" },
                requirements = Array.Empty<string>(),
                redirectUris = Array.Empty<string>(),
                postLogoutRedirectUris = Array.Empty<string>()
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }


    /// <summary>内部控制面 scope 的四条组合约束，创建与更新同一套判据</summary>
    /// <remarks>
    /// <para>钉的是<b>写入端</b>："哪些组合存得进去"。四条约束各给一个反例，外加两个必须通过的
    /// 正例——只有反例时，一条过严的断言（比如顺手要求 <c>applicationType=service</c>）
    /// 同样无人发现。</para>
    /// <para>创建入口跑完整组合；更新入口只用一个非法组合确认它走的是同一个
    /// <c>ValidateApplication</c>、没有绕过校验——两端复制整套矩阵只会多出一份要同步的清单。</para>
    /// </remarks>
#if (IncludeMultiTenancy)
    [Theory]
    [InlineData("public", new[] { "ept:token", "gt:client_credentials", "scp:tenant-routing.read" }, false)]
    [InlineData("confidential", new[] { "ept:token", "scp:tenant-routing.read" }, false)]
    [InlineData("confidential", new[] { "gt:client_credentials", "scp:tenant-routing.read" }, false)]
    [InlineData("confidential", new[] { "ept:token", "gt:client_credentials", "gt:refresh_token", "scp:tenant-routing.read" }, false)]
    [InlineData("confidential", new[] { "ept:token", "gt:client_credentials", "scp:tenant-migration.read" }, true)]
    // 未申请机器专用 scope 的普通客户端不受这套约束：缺 client_credentials 也照样存得进去
    [InlineData("confidential", new[] { "ept:token", "scp:openid" }, true)]
    public async Task Machine_only_scopes_are_accepted_only_in_a_usable_combination(
        string clientType, string[] permissions, bool expectedAccepted)
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var created = await superAdmin.Client.PostAsJsonAsync(
            "/api/v1/open-applications",
            NewApplicationPayload($"machine-{Guid.CreateVersion7():N}", clientType, permissions));

        Assert.Equal(
            expectedAccepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest,
            created.StatusCode);

        if (!expectedAccepted)
        {
            return;
        }

        // 同一判据必须同样挡住更新：先建一个合法的，再改成非法组合
        var application = await created.Content.ReadFromJsonAsync<OpenApplicationOutputDto>();
        Assert.NotNull(application);

        var updated = await superAdmin.Client.PutAsJsonAsync(
            $"/api/v1/open-applications/{application.Id}",
            new
            {
                sessionBound = false,
                displayName = "Probe",
                applicationType = "service",
                clientType = "confidential",
                // 缺 ept:token：与创建时同样必须被拒
                permissions = new[] { "gt:client_credentials", "scp:tenant-routing.read" },
                requirements = Array.Empty<string>(),
                redirectUris = Array.Empty<string>(),
                postLogoutRedirectUris = Array.Empty<string>()
            });

        Assert.Equal(HttpStatusCode.BadRequest, updated.StatusCode);
    }

    /// <summary>没登记路由 scope 的机器客户端申请不到路由令牌；Resource 因此取不到租户连接，按远端失败返回 502。</summary>
    /// <remarks>远端失败映射成 502 见 <c>ApiExceptionMappingsTests</c>。</remarks>
    [Fact]
    public async Task A_client_without_the_routing_scope_permission_gets_no_routing_token()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var clientId = $"machine-{Guid.CreateVersion7():N}";
        using var created = await superAdmin.Client.PostAsJsonAsync("/api/v1/open-applications",
            NewApplicationPayload(clientId, "confidential", ["ept:token", "gt:client_credentials", "scp:tenant-migration.read"]));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var application = await created.Content.ReadFromJsonAsync<OpenApplicationOutputDto>();
        using var reset = await superAdmin.Client.PostAsync($"/api/v1/open-applications/{application!.Id}/reset-secret", null);
        var secret = (await reset.Content.ReadFromJsonAsync<ResetOpenApplicationSecretOutputDto>())!.ClientSecret;

        using var client = CreateHttpsClient(Factory);
        async Task<HttpResponseMessage> RequestAsync(string scope) => await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret, ["scope"] = scope
            }));
        using var granted = await RequestAsync("tenant-migration.read");
        Assert.True(granted.IsSuccessStatusCode, await granted.Content.ReadAsStringAsync());
        using var denied = await RequestAsync("tenant-routing.read");
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        // ID2051：客户端无权使用该 scope（Resource 日志里看到的就是这条）
        Assert.Contains("ID2051", await denied.Content.ReadAsStringAsync());
    }
#endif


    private static object NewApplicationPayload(string clientId, string clientType, string[] permissions) =>
        new
        {
            sessionBound = false,
            clientId,
            displayName = "Probe",
            applicationType = "service",
            clientType,
            permissions,
            requirements = Array.Empty<string>(),
            redirectUris = Array.Empty<string>(),
            postLogoutRedirectUris = Array.Empty<string>()
        };


    [Fact]
    public async Task Open_application_endpoints_require_their_own_permissions()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var user = await CreateUserAsync(superAdmin.Client);
        using var session = await Factory.LoginAsync(user.Username, TestPassword);

        // 已认证不等于可管理 OAuth 客户端：开放应用持有可对外颁发令牌的凭据。
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await session.Client.GetAsync("/api/v1/open-applications?offset=0&limit=10")).StatusCode);

        await GrantAsync(
            PermissionGrantProviderNames.User,
            user.Id,
            PermissionConstant.OpenApplications.Default);
        Assert.Equal(
            HttpStatusCode.OK,
            (await session.Client.GetAsync("/api/v1/open-applications?offset=0&limit=10")).StatusCode);

        // 查看权限不含写入：创建仍被拒绝。
        var create = await session.Client.PostAsJsonAsync(
            "/api/v1/open-applications",
            new
            {
                sessionBound = false,
                clientId = $"client-{Guid.CreateVersion7():N}",
                displayName = "Probe",
                applicationType = "web",
                clientType = "confidential"
            });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }


    [Fact]
    public async Task Client_credentials_tokens_cannot_reach_user_management()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        using var machine = await CreateMachineClientAsync(
            superAdmin.Client, $"client-{Guid.CreateVersion7():N}");

        // client_credentials 的 sub 形如 client:<client_id>，代表工作负载而非人。默认策略是管理接口的兜底，
        // 它要表达的是"一个可用的自然人"——放行等于任何机器令牌都能列用户和 OAuth 客户端，
        // 而裁掉角色的项目里这些接口只剩 [Authorize]，没有第二道拦截。
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await machine.GetAsync("/api/v1/users?offset=0&limit=10")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await machine.GetAsync("/api/v1/open-applications?offset=0&limit=10")).StatusCode);
    }


    [Fact]
    public async Task Access_tokens_are_only_accepted_from_the_authorization_header()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var user = await CreateUserAsync(superAdmin.Client);
        var (clientId, clientSecret) = await CreateAuthorizationCodeClientAsync(superAdmin.Client);

        using var session = await Factory.LoginAsync(user.Username, TestPassword);
        var accessToken = await AuthorizeAndExchangeAsync(session, clientId, clientSecret);

        // 同一个有效令牌：走 Authorization 头能认证，走 query 一律不认。
        // 令牌进 URL 就会进网关访问日志、APM、浏览器历史与 Referer，RFC 6750 §2.3 因此写的是
        // "除非无法用 Authorization 头，否则 SHOULD NOT"。真需要（浏览器 WebSocket/SSE 设不了
        // 自定义头）时按路径定向搬运，见 Auth/OpenIddict/OpenIddictServerExtensions 中 AddValidation 处的说明——那是 /hubs/* 的
        // 需要，不是全部 API 的。这条断言锁住这个决定：谁把全局提取重新打开，这里会红。
        using var viaHeader = CreateHttpsClient();
        viaHeader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        Assert.Equal(HttpStatusCode.OK, (await viaHeader.GetAsync("/api/v1/auth/me")).StatusCode);

        using var viaQuery = CreateHttpsClient();
        var queryResponse = await viaQuery.GetAsync(
            $"/api/v1/auth/me?access_token={Uri.EscapeDataString(accessToken)}");

        Assert.Equal(HttpStatusCode.Unauthorized, queryResponse.StatusCode);

        // form-body 那一半（RFC 6750 §2.2）同样关掉了，一并锁住：只关一半、或者只测一半，
        // 另一条路就会在无人察觉的情况下重新打开。
        // 断言 401 而不是别的 4xx：401 说明请求根本没通过认证。若提取还开着，令牌会被采信，
        // 请求就会走到模型绑定——表单体喂给 [FromBody] 的 JSON 参数，拿到的是另一种 4xx。
        using var viaForm = CreateHttpsClient();
        var formResponse = await viaForm.PostAsync(
            "/api/v1/auth/change-password",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("access_token", accessToken)]));

        Assert.Equal(HttpStatusCode.Unauthorized, formResponse.StatusCode);
    }


    [Fact]
    public async Task An_invalid_bearer_header_is_rejected_even_with_a_valid_cookie_session()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var user = await CreateUserAsync(superAdmin.Client);
        using var session = await Factory.LoginAsync(user.Username, TestPassword);

        // 显式 Authorization 头选择 Bearer；失败不能回退到 Cookie。
        session.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "not-a-real-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.Client.GetAsync("/api/v1/auth/me")).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await superAdmin.Client.PatchAsync($"/api/v1/users/{user.Id}/disable", null)).StatusCode);

        // 会话已随停用撤销，请求里只剩那个无效的 Bearer，按它答 invalid_token 的挑战是标准行为
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }


    [Fact]
    public async Task Client_credentials_cannot_impersonate_a_user_by_taking_their_id_as_client_id()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var victim = await CreateUserAsync(superAdmin.Client);
        await GrantAsync(PermissionGrantProviderNames.User, victim.Id, PermissionConstant.Users.Default);

        // 攻击者挑一个已存在的用户 Id 当 client_id：用户 Id 从用户管理、审计日志或业务数据里
        // 都拿得到，碰撞不是偶然而是被挑出来的。只要机器令牌的 sub 与人类主体共用一个
        // 命名空间，sub 就会被解析成这个用户，机器令牌随之继承他的直授、角色乃至超管身份。
        using var machine = await CreateMachineClientAsync(superAdmin.Client, victim.Id.ToString());

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await machine.GetAsync("/api/v1/users?offset=0&limit=10")).StatusCode);
    }


    [Fact]
    public async Task Disabling_a_user_revokes_their_already_issued_bearer_token()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var user = await CreateUserAsync(superAdmin.Client);
        var (clientId, clientSecret) = await CreateAuthorizationCodeClientAsync(superAdmin.Client);

        using var session = await Factory.LoginAsync(user.Username, TestPassword);
        var accessToken = await AuthorizeAndExchangeAsync(session, clientId, clientSecret);

        using var api = CreateHttpsClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/connect/userinfo")).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await superAdmin.Client.PatchAsync($"/api/v1/users/{user.Id}/disable", null)).StatusCode);

        // Cookie、OpenIddict Validation、userinfo 是三条不同的认证路径。撤权承诺覆盖 API 令牌，
        // 不只是浏览器会话——只有 Cookie 一条测试保持绿色时，策略被改窄了也看不出来。
        var revoked = await api.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);

        // 401 必须带 challenge：RFC 9110 对此是 MUST，OAuth 客户端也据此把响应识别为
        // "令牌失效、去重新取"，而不是当成一个普通业务错误重试到底。
        var challenge = Assert.Single(revoked.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", challenge.Scheme);
        Assert.Contains("invalid_token", challenge.Parameter);


        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/connect/userinfo")).StatusCode);
    }


    /// <summary>客户端密钥只在创建与重置时各返回一次明文，查询路径一律不回。</summary>
    /// <remarks>
    /// 这条钉的是输出映射：应用实体上存的是<b>散列后</b>的密钥，而输出 DTO 有同名
    /// <c>ClientSecret</c> 字段——按同名约定自动映射就会把存储值写进查询响应。
    /// 断言走真实 HTTP 端点而不是映射单测：泄漏与否是接口契约，只有响应体能证明。
    /// </remarks>
    [Fact]
    public async Task Client_secret_is_returned_only_on_create_and_reset()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var clientId = $"secret-probe-{Guid.NewGuid():N}"[..24];

        var created = await superAdmin.Client.PostAsJsonAsync(
            "/api/v1/open-applications",
            new
            {
                sessionBound = false,
                clientId,
                displayName = "Secret probe",
                applicationType = "service",
                clientType = "confidential",
                permissions = new[] { "ept:token", "gt:client_credentials" },
                requirements = Array.Empty<string>(),
                redirectUris = Array.Empty<string>(),
                postLogoutRedirectUris = Array.Empty<string>()
            });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var createdApp = await created.Content.ReadFromJsonAsync<OpenApplicationOutputDto>();
        Assert.NotNull(createdApp);
        // 创建：明文返回一次
        Assert.False(string.IsNullOrEmpty(createdApp.ClientSecret));
        Assert.True(createdApp.HasClientSecret);

        // 查询：不回密钥，但要能看出配过。
        // 只断言单条：分页列表的每一项走的是同一个 MapToOutputAsync，覆盖同一代码路径
        var fetched = await superAdmin.Client.GetFromJsonAsync<OpenApplicationOutputDto>(
            $"/api/v1/open-applications/{createdApp.Id}");
        Assert.NotNull(fetched);
        Assert.Null(fetched.ClientSecret);
        Assert.True(fetched.HasClientSecret);
    }

    /// <summary>注册一个 client_credentials 应用，取令牌，返回已带 Bearer 的客户端。</summary>
    private async Task<HttpClient> CreateMachineClientAsync(HttpClient superAdminClient, string clientId)
    {
        var created = await superAdminClient.PostAsJsonAsync(
            "/api/v1/open-applications",
            new
            {
                sessionBound = false,
                clientId,
                displayName = "Workload",
                applicationType = "service",
                clientType = "confidential",
                permissions = new[] { "ept:token", "gt:client_credentials", $"scp:{ApiScope}" },
                requirements = Array.Empty<string>(),
                redirectUris = Array.Empty<string>(),
                postLogoutRedirectUris = Array.Empty<string>()
            });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var application = await created.Content.ReadFromJsonAsync<OpenApplicationOutputDto>();
        Assert.NotNull(application);

        var reset = await superAdminClient.PostAsync(
            $"/api/v1/open-applications/{application.Id}/reset-secret", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var secret = await reset.Content.ReadFromJsonAsync<ResetOpenApplicationSecretOutputDto>();
        Assert.NotNull(secret);

        var machine = CreateHttpsClient();
        var token = await machine.PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", secret.ClientSecret),
            // 申请本服务 API 的 scope：令牌的受众是本服务，才轮得到后面的授权策略判定
            new KeyValuePair<string, string>("scope", ApiScope)
        ]));
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);

        var payload = await token.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(payload);
        machine.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", payload.AccessToken);

        return machine;
    }


    private static async Task<(string ClientId, string ClientSecret)> CreateAuthorizationCodeClientAsync(
        HttpClient superAdminClient)
    {
        var clientId = $"client-{Guid.CreateVersion7():N}";
        var created = await superAdminClient.PostAsJsonAsync(
            "/api/v1/open-applications",
            new
            {
                sessionBound = false,
                clientId,
                displayName = "Bearer probe",
                applicationType = "web",
                clientType = "confidential",
                permissions = new[]
                {
                    "ept:authorization", "ept:token", "gt:authorization_code", "rst:code", "scp:openid", $"scp:{ApiScope}"
                },
                requirements = Array.Empty<string>(),
                redirectUris = new[] { RedirectUri },
                postLogoutRedirectUris = Array.Empty<string>()
            });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var application = await created.Content.ReadFromJsonAsync<OpenApplicationOutputDto>();
        Assert.NotNull(application);

        var reset = await superAdminClient.PostAsync(
            $"/api/v1/open-applications/{application.Id}/reset-secret", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var secret = await reset.Content.ReadFromJsonAsync<ResetOpenApplicationSecretOutputDto>();
        Assert.NotNull(secret);

        return (clientId, secret.ClientSecret);
    }


    /// <summary>用登录态跑一遍授权码流程，换出该用户的 access token。</summary>
    /// <remarks>
    /// 授权端点没有同意页：Cookie 有效就直接 SignIn 并 302 回带 code 的回调地址，
    /// 所以这一段比"引入 password grant"便宜得多，也不必为测试放宽服务端配置。
    /// 服务端启用了 RequireProofKeyForCodeExchange，因此 code_challenge 必须是真的 S256。
    /// </remarks>
    private async Task<string> AuthorizeAndExchangeAsync(
        AuthenticatedSession session, string clientId, string clientSecret)
    {
        var verifier = Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N");
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        // 另起一个客户端而不是改 session.Client：后者已经发过登录请求，BaseAddress 不再可写。
        // 带上同一份 Cookie，授权端点才认得出登录态。
        using var browser = CreateHttpsClient();
        browser.DefaultRequestHeaders.Add("Cookie", session.Cookie);

        var authorize = await browser.GetCachedAsync(
            "/connect/authorize" +
            $"?client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&response_type=code&scope=openid%20{ApiScope}" +
            $"&code_challenge={challenge}&code_challenge_method=S256");

        Assert.Equal(HttpStatusCode.Found, authorize.StatusCode);

        var code = authorize.Headers.Location!.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0] == "code")
            .Select(pair => Uri.UnescapeDataString(pair[1]))
            .SingleOrDefault();
        Assert.False(string.IsNullOrWhiteSpace(code));

        using var exchange = CreateHttpsClient();
        var token = await exchange.PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new KeyValuePair<string, string>("code", code!),
            new KeyValuePair<string, string>("redirect_uri", RedirectUri),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret),
            new KeyValuePair<string, string>("code_verifier", verifier)
        ]));
        Assert.True(token.IsSuccessStatusCode, await token.Content.ReadAsStringAsync());

        var payload = await token.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(payload);

        return payload.AccessToken;
    }


    /// <summary>基地址为 https 的客户端。</summary>
    /// <remarks>
    /// OpenIddict 的授权与令牌端点只收 HTTPS。TestServer 不做真实 TLS，改基地址即可让
    /// <c>Request.IsHttps</c> 成立，不必为测试在服务端放宽这条要求——那等于把生产配置改松来迁就测试。
    /// </remarks>
    private HttpClient CreateHttpsClient()
    {
        var client = Factory.CreateProjectClient();
        client.BaseAddress = new Uri("https://localhost");

        return client;
    }
}
#endif
