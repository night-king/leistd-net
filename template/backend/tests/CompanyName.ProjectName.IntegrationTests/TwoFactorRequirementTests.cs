#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
#if (OpenIddictServer)
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using CompanyName.ProjectName.Application.Auth.Options;
using Microsoft.AspNetCore.WebUtilities;
#endif
using CompanyName.ProjectName.Application.Settings.Provider;
#if (IncludeMultiTenancy)
using Leistd.MultiTenancy.AspNetCore.Options;
#endif
using Leistd.Security.OneTimeCodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>"要求两步验证"对已有会话即时生效：策略与本人的两步验证状态每个请求重新判定，不靠会话声明，也不跟着有效性缓存。</summary>
/// <remarks>
/// 会话有效性缓存一分钟。下面的用例都先让同一个 Cookie 打出缓存，再改策略或状态：命中缓存时跳过受限判定、
/// 或把两步验证状态放进缓存，都会让这些用例失败。
/// </remarks>
public sealed class TwoFactorRequirementTests(ProjectWebApplicationFactory factory) : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Password = "Requirement!Passw0rd";
    private const string GuardedUrl = "/api/v1/auth/me/sessions";

    [Fact]
    public async Task Turning_the_requirement_on_and_off_applies_to_a_warm_session_on_the_next_request()
    {
        var (host, _) = CreateHost();
        using var _host = host;
        var username = await CreateUserAsync(host, "req_toggle");
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        // 有效性进缓存：之后同一个 Cookie 都命中缓存
        await AssertUnrestrictedAsync(session.Client);
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync("/api/v1/users?offset=0&limit=1")).StatusCode);

        await RequireTwoFactorAsync(host, true);
        try
        {
            await AssertRestrictedAsync(session.Client);
            // 打开开关的管理员自己没启用两步验证，同样立即受限
            using (var adminBlocked = await admin.Client.GetAsync("/api/v1/users?offset=0&limit=1"))
            {
                Assert.Equal(HttpStatusCode.Forbidden, adminBlocked.StatusCode);
                Assert.Equal("Auth:TwoFactorSetupRequired", await ErrorCodeAsync(adminBlocked));
            }
        }
        finally
        {
            await RequireTwoFactorAsync(host, false);
        }

        await AssertUnrestrictedAsync(session.Client);
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync("/api/v1/users?offset=0&limit=1")).StatusCode);
    }

    /// <summary>
    /// 旧读取停在回填缓存之前，期间本人完成设置并提交，旧读取再写回缓存：提交之后的新请求仍要读到"已启用"。
    /// </summary>
    /// <remarks>
    /// 停在有效性缓存的写入处：这一刻旧请求已读到"未启用"。两步验证状态若跟着有效性一起缓存，
    /// 这里写回的就是旧状态，新请求命中它后仍被当成受限，直到缓存过期。
    /// </remarks>
    [Fact]
    public async Task A_stale_read_cannot_bring_back_the_restriction_after_setup_completes()
    {
        var gate = new InterleavingGate();
        var (host, clock) = CreateHost(services =>
            InterleavingGate.Wrap<IDistributedCache>(services, inner => new SessionCacheGate(gate, inner)));
        using var _host = host;
        var username = await CreateUserAsync(host, "req_backfill");
        await RequireTwoFactorAsync(host, true);
        try
        {
            using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);

            // 登录之后的第一个请求缓存未命中：读到"未启用"后停在回填之前
            gate.Arm();
            var stale = session.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
            await gate.Entered;

            var setup = await BeginSetupAsync(session.Client);
            using (var enable = await session.Client.PostAsJsonAsync(
                       "/api/v1/auth/me/two-factor/enable", new { setup.SetupId, Code = Code(setup.Secret, clock) }))
            {
                Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
            }

            gate.Release();
            Assert.True((await stale).GetProperty("twoFactorSetupRequired").GetBoolean());

            // 变更提交之后发出的请求：不受限，与旧读取写回了什么无关
            await AssertUnrestrictedAsync(session.Client);
        }
        finally
        {
            gate.Release();
            await RequireTwoFactorAsync(host, false);
        }
    }

    // 本人完成设置：同一个 Cookie 的下一个请求即解除，接口与"当前用户"一致
    [Fact]
    public async Task Completing_setup_lifts_the_restriction_on_the_same_cookie()
    {
        var (host, clock) = CreateHost();
        using var _host = host;
        var username = await CreateUserAsync(host, "req_complete");
        await RequireTwoFactorAsync(host, true);
        try
        {
            using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
            await AssertRestrictedAsync(session.Client);

            var setup = await BeginSetupAsync(session.Client);
            using (var enable = await session.Client.PostAsJsonAsync(
                       "/api/v1/auth/me/two-factor/enable", new { setup.SetupId, Code = Code(setup.Secret, clock) }))
            {
                Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
            }

            await AssertUnrestrictedAsync(session.Client);
        }
        finally
        {
            await RequireTwoFactorAsync(host, false);
        }
    }
#if (IncludeMultiTenancy)

    // 策略按会话所属的租户读：宿主开着不影响租户会话，反过来也一样
    [Fact]
    public async Task Host_and_tenant_requirements_apply_to_their_own_sessions()
    {
        var (host, _) = CreateHost();
        using var _host = host;
        var username = await CreateUserAsync(host, "req_host_user");
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var tenantId = await CreateTenantAsync(admin.Client, $"req{Guid.NewGuid():N}"[..20]);
        using var hostSession = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        using var tenantSession = await LoginTenantAdminAsync(host, tenantId);
        await AssertUnrestrictedAsync(hostSession.Client);
        await AssertUnrestrictedAsync(tenantSession);

        await RequireTwoFactorAsync(host, true);
        try
        {
            await AssertRestrictedAsync(hostSession.Client);
            await AssertUnrestrictedAsync(tenantSession);
        }
        finally
        {
            await RequireTwoFactorAsync(host, false);
        }

        await RequireTwoFactorAsync(host, true, tenantId);
        try
        {
            await AssertUnrestrictedAsync(hostSession.Client);
            await AssertRestrictedAsync(tenantSession);
        }
        finally
        {
            await RequireTwoFactorAsync(host, false, tenantId);
        }
    }
#endif
#if (IncludeNotifications || IncludeRealTime)

    // 受限会话建不起新的实时连接：握手（negotiate）就被拒
    [Fact]
    public async Task A_restricted_session_cannot_open_a_new_hub_connection()
    {
        const string Negotiate = ProjectWebApplicationFactory.HubPath + "/negotiate?negotiateVersion=1";
        var (host, _) = CreateHost();
        using var _host = host;
        var username = await CreateUserAsync(host, "req_hub");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        using (var allowed = await session.Client.PostAsync(Negotiate, null))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        await RequireTwoFactorAsync(host, true);
        try
        {
            using var blocked = await session.Client.PostAsync(Negotiate, null);
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            Assert.Equal("Auth:TwoFactorSetupRequired", await ErrorCodeAsync(blocked));
        }
        finally
        {
            await RequireTwoFactorAsync(host, false);
        }
    }
#endif
#if (OpenIddictServer)

    /// <summary>
    /// 授权端点匿名可达，受限会话不能借它拿到授权码：交互请求先去设置页、带着同一个缓存请求回来；
    /// 静默请求按协议返回 <c>interaction_required</c>。
    /// </summary>
    [Fact]
    public async Task A_restricted_session_gets_no_authorization_code_until_setup_is_done()
    {
        var (host, _) = CreateHost();
        using var _host = host;
        var username = await CreateUserAsync(host, "req_oidc");
        string clientId;
        using (var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword))
        {
            clientId = await CreatePublicClientAsync(admin.Client);
        }
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        using var browser = Browser(host, session.Cookie);

        await RequireTwoFactorAsync(host, true);
        string returnUrl;
        try
        {
            using (var silent = await browser.GetCachedAsync(AuthorizationUrl(clientId, "&prompt=none&state=probe")))
            {
                Assert.Equal(HttpStatusCode.Found, silent.StatusCode);
                Assert.StartsWith(Callback, silent.Headers.Location!.AbsoluteUri);
                var query = QueryHelpers.ParseQuery(silent.Headers.Location.Query);
                Assert.Equal("interaction_required", query["error"].ToString());
                Assert.False(query.ContainsKey("code"));
            }

            using var interactive = await browser.GetCachedAsync(AuthorizationUrl(clientId, "&state=probe"));
            Assert.Equal(HttpStatusCode.Found, interactive.StatusCode);
            var location = new Uri(browser.BaseAddress!, interactive.Headers.Location!);
            Assert.Equal("/auth/two-factor-setup", location.AbsolutePath);
            returnUrl = QueryHelpers.ParseQuery(location.Query)["returnUrl"].ToString();
            // 回跳是同一个缓存请求的本地地址：协议参数都在 request token 里
            var target = new Uri(browser.BaseAddress!, returnUrl);
            Assert.Equal("/connect/authorize", target.AbsolutePath);
            Assert.Equal(["client_id", "request_uri"], QueryHelpers.ParseQuery(target.Query).Keys.Order());
        }
        finally
        {
            await RequireTwoFactorAsync(host, false);
        }

        // 限制解除后整页回到这个地址，授权照常完成
        using var resumed = await browser.GetAsync(returnUrl);
        Assert.Equal(HttpStatusCode.Found, resumed.StatusCode);
        Assert.StartsWith(Callback, resumed.Headers.Location!.AbsoluteUri);
        Assert.True(QueryHelpers.ParseQuery(resumed.Headers.Location.Query).ContainsKey("code"));
    }

    private const string Callback = "https://localhost/oidc-test";

    private static async Task<string> CreatePublicClientAsync(HttpClient admin)
    {
        var id = $"req-{Guid.NewGuid():N}";
        using var response = await admin.PostAsJsonAsync("/api/v1/open-applications", new
        {
            sessionBound = false,
            clientId = id,
            applicationType = "web",
            clientType = "public",
            redirectUris = new[] { Callback },
            permissions = new[] { "ept:authorization", "ept:token", "gt:authorization_code", "rst:code", "scp:openid", $"scp:{new OAuthResourceOptions().Resource}" },
            requirements = new[] { "ft:pkce" }
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return id;
    }

    private static HttpClient Browser(WebApplicationFactory<Program> host, string cookie)
    {
        // 授权端点只收 HTTPS：TestServer 不做真实 TLS，改基地址即可
        var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        client.BaseAddress = new Uri("https://localhost");
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static string AuthorizationUrl(string client, string parameters) =>
        $"/connect/authorize?client_id={client}&redirect_uri={Uri.EscapeDataString(Callback)}&response_type=code" +
        $"&scope=openid%20{new OAuthResourceOptions().Resource}" +
        $"&code_challenge={Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(new string('x', 64))))}" +
        $"&code_challenge_method=S256{parameters}";
#endif

    // 受限：服务端只放行设置所需的接口，"当前用户"给出同一个结论
    private static async Task AssertRestrictedAsync(HttpClient client)
    {
        using (var blocked = await client.GetAsync(GuardedUrl))
        {
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            Assert.Equal("Auth:TwoFactorSetupRequired", await ErrorCodeAsync(blocked));
        }

        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.True(me.GetProperty("twoFactorSetupRequired").GetBoolean());
    }

    private static async Task AssertUnrestrictedAsync(HttpClient client)
    {
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(GuardedUrl)).StatusCode);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.False(me.GetProperty("twoFactorSetupRequired").GetBoolean());
    }

    // 直接写设置：经接口写入的管理员会话自己也会受限，撤回的请求就发不出去了
    private static Task RequireTwoFactorAsync(WebApplicationFactory<Program> host, bool required, Guid? tenantId = null) =>
        ProjectWebApplicationFactory.SetTenantSettingAsync(
            host, SettingConstant.Security.RequireTwoFactor, required ? "true" : null, tenantId);
#if (IncludeMultiTenancy)

    private static async Task<Guid> CreateTenantAsync(HttpClient hostAdmin, string name)
    {
        using var response = await hostAdmin.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = name,
            DisplayName = $"{name} Inc.",
            AdminEmail = $"admin@{name}.example.com",
            AdminPassword = Password
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<HttpClient> LoginTenantAdminAsync(WebApplicationFactory<Program> host, Guid tenantId)
    {
        var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        client.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, tenantId.ToString());
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/session-login", new { UsernameOrEmail = "admin", Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie",
            string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';', 2)[0])));
        return client;
    }
#endif

    private (WebApplicationFactory<Program> Host, FakeTimeProvider Clock) CreateHost(Action<IServiceCollection>? configure = null)
    {
        // 从此刻起步：缓存条目按真实时钟回收，假时钟落在过去会让刚写入的设置挑战被当成已过期
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            configure?.Invoke(services);
        }));
        return (host, clock);
    }

    private static async Task<string> CreateUserAsync(WebApplicationFactory<Program> host, string prefix)
    {
        var username = $"{prefix}_{Guid.NewGuid():N}"[..30];
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var create = await admin.Client.PostAsJsonAsync("/api/v1/users", new
        {
            Username = username,
            Email = $"{username}@example.test",
            Password,
            IsActive = true
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        return username;
    }

    private static async Task<(Guid SetupId, string Secret)> BeginSetupAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/me/two-factor/setup", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (body.RootElement.GetProperty("setupId").GetGuid(), body.RootElement.GetProperty("secret").GetString()!);
    }

    private static string Code(string secret, FakeTimeProvider clock) =>
        Totp.ComputeCode(Totp.ParseSecret(secret)!, Totp.TimeStepAt(clock.GetUtcNow()));

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    // 只拦会话有效性缓存的写入（回填）；其余缓存读写照常
    private sealed class SessionCacheGate(InterleavingGate gate, IDistributedCache inner) : IDistributedCache
    {
        public byte[]? Get(string key) => inner.Get(key);

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => inner.GetAsync(key, token);

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => inner.Set(key, value, options);

        public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            if (key.StartsWith("auth:session:", StringComparison.Ordinal))
                await gate.PassAsync();
            await inner.SetAsync(key, value, options, token);
        }

        public void Refresh(string key) => inner.Refresh(key);

        public Task RefreshAsync(string key, CancellationToken token = default) => inner.RefreshAsync(key, token);

        public void Remove(string key) => inner.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default) => inner.RemoveAsync(key, token);
    }
}
#endif
