#if (ExternalLogin)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Api.Auth.Authentication;
using CompanyName.ProjectName.Api.Options;
using CompanyName.ProjectName.Application.Settings.Provider;
#if (IncludeMultiTenancy)
using CompanyName.ProjectName.Application.Settings.Errors;
using Leistd.MultiTenancy.AspNetCore.Options;
#endif
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Serilog.Events;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>外部登录由系统设置启停与配置：开关打开且凭据齐全才提供入口，改动在运行期生效，凭据不全只是不可用。</summary>
/// <remarks>
/// 每个用例一个夹具：设置写进库里，派生宿主共用同一个库，用例之间不能互相留下覆盖值。
/// 协议往返用真实的官方处理器，只替换 Backchannel（见 <see cref="ExternalOAuthBackchannel"/>）。
/// </remarks>
public sealed class ExternalLoginSettingsTests
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("true", "deploy-client", null)]
    [InlineData("true", null, "deploy-secret-value")]
    [InlineData("false", "deploy-client", "deploy-secret-value")]
    public async Task A_disabled_or_incomplete_deployment_starts_and_offers_no_provider(string? enabled, string? clientId, string? clientSecret)
    {
        using var factory = new ProjectWebApplicationFactory();
        var logger = new FakeLogger<ExternalSchemeSynchronizer>();
        using var host = factory.WithWebHostBuilder(builder =>
        {
            if (enabled is not null) builder.UseSetting("ExternalAuth:Github:Enabled", enabled);
            if (clientId is not null) builder.UseSetting("ExternalAuth:Github:ClientId", clientId);
            if (clientSecret is not null) builder.UseSetting("ExternalAuth:Github:ClientSecret", clientSecret);
            builder.ConfigureTestServices(services => services.AddSingleton<ILogger<ExternalSchemeSynchronizer>>(logger));
        });

        // 不可用的方案若仍登记着，认证中间件初始化它时会因凭据为空抛出，任何请求（含登录）都会失败
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        Assert.Empty(await ProvidersAsync(admin.Client));

        var github = await StatusAsync(admin.Client, "github");
        Assert.Equal(enabled == "true", github.GetProperty("enabled").GetBoolean());
        Assert.Equal(clientId is not null, github.GetProperty("hasClientId").GetBoolean());
        // 部署配置提供的密钥也算已配置：设置页的机密项只反映存储里的覆盖值，看不出这一点
        Assert.Equal(clientSecret is not null, github.GetProperty("hasClientSecret").GetBoolean());
        Assert.False(github.GetProperty("active").GetBoolean());

        var warnings = logger.Collector.GetSnapshot().Where(record => record.Level == LogLevel.Warning).Select(record => record.Message).ToList();
        if (enabled == "true")
        {
            Assert.NotEmpty(warnings);
            Assert.All(warnings, warning =>
            {
                Assert.Contains(clientId is null ? "ExternalAuth:Github:ClientId" : "ExternalAuth:Github:ClientSecret", warning);
                Assert.DoesNotContain("deploy-secret-value", warning);
                Assert.DoesNotContain("deploy-client", warning);
            });
        }
        else
        {
            Assert.Empty(warnings);
        }
    }

    [Fact]
    public async Task Settings_enable_rotate_and_disable_a_provider_at_runtime()
    {
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel();
        using var host = backchannel.CreateHost(factory, deployed: false);
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var browser = ProjectWebApplicationFactory.CreateProjectClient(host);

        // 只打开开关：凭据不全，入口不出现；写入本身不被拒绝
        await WriteAsync(admin.Client, SettingConstant.ExternalLogin.GithubEnabled, "true");
        Assert.Empty(await ProvidersAsync(browser));

        await WriteAsync(admin.Client, SettingConstant.ExternalLogin.GithubClientId, "runtime-client");
        await WriteAsync(admin.Client, SettingConstant.ExternalLogin.GithubClientSecret, "runtime-secret");
        Assert.Equal(["github"], await ProvidersAsync(browser));
        Assert.Equal("runtime-client", await ChallengeClientIdAsync(browser));
        await ExternalOAuthBackchannel.StartAsync(browser);
        Assert.Equal(("runtime-client", "runtime-secret"), (backchannel.ClientId, backchannel.ClientSecret));

        // 保持启用时换凭据：下一次授权与换 token 都用新值，不是首次构造时缓存下来的旧值
        await WriteAsync(admin.Client, SettingConstant.ExternalLogin.GithubClientId, "rotated-client");
        await WriteAsync(admin.Client, SettingConstant.ExternalLogin.GithubClientSecret, "rotated-secret");
        Assert.Equal("rotated-client", await ChallengeClientIdAsync(browser));
        await ExternalOAuthBackchannel.StartAsync(browser);
        Assert.Equal(("rotated-client", "rotated-secret"), (backchannel.ClientId, backchannel.ClientSecret));

        await WriteAsync(admin.Client, SettingConstant.ExternalLogin.GithubEnabled, "false");
        Assert.Empty(await ProvidersAsync(browser));
        using var challenge = await browser.GetAsync("/api/v1/external-auth/github/challenge");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, challenge.StatusCode);
    }

    [Fact]
    public async Task Clearing_a_credential_override_falls_back_to_the_deployment()
    {
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel();
        using var host = backchannel.CreateHost(factory);
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var browser = ProjectWebApplicationFactory.CreateProjectClient(host);

        await WriteAsync(admin.Client, SettingConstant.ExternalLogin.GithubClientSecret, "override-secret");
        await ExternalOAuthBackchannel.StartAsync(browser);
        Assert.Equal("override-secret", backchannel.ClientSecret);

        // 设置页的清空与恢复默认都写 null：回到部署配置里的密钥，提供方继续可用
        await WriteAsync(admin.Client, SettingConstant.ExternalLogin.GithubClientSecret, null);
        Assert.Equal(["github", "google"], await ProvidersAsync(browser));
        await ExternalOAuthBackchannel.StartAsync(browser);
        Assert.Equal("protocol-test-secret", backchannel.ClientSecret);
    }

    [Fact]
    public async Task Clearing_a_credential_without_a_deployment_value_withdraws_only_that_provider()
    {
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel();
        using var host = backchannel.CreateHost(factory, deployed: false);
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var browser = ProjectWebApplicationFactory.CreateProjectClient(host);
        await EnableAsync(admin.Client, SettingConstant.ExternalLogin.GithubEnabled, SettingConstant.ExternalLogin.GithubClientId, SettingConstant.ExternalLogin.GithubClientSecret);
        await EnableAsync(admin.Client, SettingConstant.ExternalLogin.GoogleEnabled, SettingConstant.ExternalLogin.GoogleClientId, SettingConstant.ExternalLogin.GoogleClientSecret);
        Assert.Equal(["github", "google"], await ProvidersAsync(browser));

        await WriteAsync(admin.Client, SettingConstant.ExternalLogin.GithubClientSecret, null);
        Assert.Equal(["google"], await ProvidersAsync(browser));
        using (var response = await browser.GetAsync("/api/v1/external-auth/google/challenge"))
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        // 开关一直开着，补齐凭据即恢复
        await WriteAsync(admin.Client, SettingConstant.ExternalLogin.GithubClientSecret, "runtime-secret");
        Assert.Equal(["github", "google"], await ProvidersAsync(browser));
    }

    [Fact]
    public async Task An_incomplete_provider_does_not_hold_back_other_host_settings()
    {
        using var factory = new ProjectWebApplicationFactory();
        using var admin = await ProjectWebApplicationFactory.LoginAsync(factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var requestLogging = factory.Services.GetRequiredService<IOptionsMonitor<RequestLoggingOptions>>();
        Assert.NotEqual(LogEventLevel.Warning, requestLogging.CurrentValue.Level);

        await WriteAsync(admin.Client, SettingConstant.ExternalLogin.GithubEnabled, "true");
        await WriteAsync(admin.Client, SettingConstant.Logging.RequestLevel, nameof(LogEventLevel.Warning));

        // 外部登录不参与整组校验：同批应用的其他设置照常被消费方读到
        Assert.Equal(LogEventLevel.Warning, requestLogging.CurrentValue.Level);
        Assert.Empty(await ProvidersAsync(admin.Client));
    }

    [Fact]
    public async Task A_restarted_instance_offers_stored_providers_before_its_first_request()
    {
        using var factory = new ProjectWebApplicationFactory();
        using (var admin = await ProjectWebApplicationFactory.LoginAsync(factory, "admin", ProjectWebApplicationFactory.TestAdminPassword))
            await EnableAsync(admin.Client, SettingConstant.ExternalLogin.GithubEnabled, SettingConstant.ExternalLogin.GithubClientId, SettingConstant.ExternalLogin.GithubClientSecret);

        // 派生宿主是同一个库上新起的实例：存储里的设置在它接收第一个请求前就已同步成认证方案
        using var restarted = factory.WithWebHostBuilder(_ => { });
        using var browser = ProjectWebApplicationFactory.CreateProjectClient(restarted);
        Assert.Equal(["github"], await ProvidersAsync(browser));
    }

    [Fact]
    public async Task A_restarted_instance_with_an_incomplete_stored_provider_still_serves_the_settings_page()
    {
        using var factory = new ProjectWebApplicationFactory();
        using (var admin = await ProjectWebApplicationFactory.LoginAsync(factory, "admin", ProjectWebApplicationFactory.TestAdminPassword))
        {
            await WriteAsync(admin.Client, SettingConstant.ExternalLogin.GithubEnabled, "true");
            await WriteAsync(admin.Client, SettingConstant.ExternalLogin.GithubClientId, "runtime-client");
        }

        // 写入时不拦截不完整的组合，重启后也不能因此起不来：设置页照常可用，管理员可以接着补齐
        using var restarted = factory.WithWebHostBuilder(_ => { });
        using var restartedAdmin = await ProjectWebApplicationFactory.LoginAsync(restarted, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        Assert.Empty(await ProvidersAsync(restartedAdmin.Client));
        var github = await StatusAsync(restartedAdmin.Client, "github");
        Assert.True(github.GetProperty("enabled").GetBoolean());
        Assert.True(github.GetProperty("hasClientId").GetBoolean());
        Assert.False(github.GetProperty("hasClientSecret").GetBoolean());
        Assert.False(github.GetProperty("active").GetBoolean());

        await WriteAsync(restartedAdmin.Client, SettingConstant.ExternalLogin.GithubClientSecret, "runtime-secret");
        Assert.Equal(["github"], await ProvidersAsync(restartedAdmin.Client));
    }

    [Fact]
    public async Task Disabling_closes_new_entries_while_an_issued_ticket_still_completes()
    {
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel();
        using var host = backchannel.CreateHost(factory);
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var issued = ProjectWebApplicationFactory.CreateProjectClient(host);
        var flow = await ExternalOAuthBackchannel.StartAsync(issued);
        using var pending = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var redirect = await pending.GetAsync("/api/v1/external-auth/github/challenge");
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        var state = QueryHelpers.ParseQuery(redirect.Headers.Location!.Query)["state"].ToString();

        await WriteAsync(admin.Client, SettingConstant.ExternalLogin.GithubEnabled, "false");

        // 已跳到提供方、停用后才回调：方案已移除，回调路径落到 /api 的兜底
        using var callback = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/external-auth/github/signin?code={Guid.NewGuid():N}&state={Uri.EscapeDataString(state)}");
        callback.Headers.Add("Cookie", ExternalOAuthBackchannel.Cookies(redirect));
        using (var response = await pending.SendAsync(callback))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using (var response = await pending.GetAsync("/api/v1/external-auth/github/challenge"))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        // 已签发的外部票据不被撤销，有效期内仍可完成
        issued.DefaultRequestHeaders.Add("Cookie", flow.Cookie);
        using var complete = await issued.PostAsJsonAsync("/api/v1/external-auth/github/complete", new { });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
    }

    [Fact]
    public async Task Status_shows_presence_and_the_callback_address_without_credentials()
    {
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel();
        using var host = backchannel.CreateHost(factory);
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        await WriteAsync(admin.Client, SettingConstant.ExternalLogin.GoogleClientSecret, "stored-secret-value");

        using var response = await admin.Client.GetAsync("/api/v1/settings/external-login/status");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("protocol-test-secret", body);
        Assert.DoesNotContain("stored-secret-value", body);
        Assert.DoesNotContain("protocol-test-client", body);

        using var document = JsonDocument.Parse(body);
        var providers = document.RootElement.GetProperty("providers").EnumerateArray().ToList();
        Assert.Equal(["github", "google"], providers.Select(item => item.GetProperty("provider").GetString()!));
        Assert.All(providers, item =>
        {
            Assert.True(item.GetProperty("enabled").GetBoolean());
            Assert.True(item.GetProperty("hasClientId").GetBoolean());
            Assert.True(item.GetProperty("hasClientSecret").GetBoolean());
            Assert.True(item.GetProperty("active").GetBoolean());
        });

        // 回调地址与发起登录时交给提供方的 redirect_uri 完全一致
        using var browser = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var challenge = await browser.GetAsync("/api/v1/external-auth/github/challenge");
        var redirectUri = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["redirect_uri"].ToString();
        Assert.Equal(redirectUri, providers[0].GetProperty("callbackUrl").GetString());
    }

    [Fact]
    public async Task Status_behind_a_trusted_proxy_reports_the_public_https_callback()
    {
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel();
        using var host = backchannel.CreateHost(factory).WithWebHostBuilder(builder => builder.UseSetting("ForwardedHeaders:KnownProxies:0", "127.0.0.1"));
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var browser = ProjectWebApplicationFactory.CreateProjectClient(host);

        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/settings/external-login/status");
        statusRequest.Headers.Add("X-Forwarded-Proto", "https");
        using var status = await admin.Client.SendAsync(statusRequest);
        var callbackUrl = (await status.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("providers")[0].GetProperty("callbackUrl").GetString();

        using var challengeRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/external-auth/github/challenge");
        challengeRequest.Headers.Add("X-Forwarded-Proto", "https");
        using var challenge = await browser.SendAsync(challengeRequest);
        var redirectUri = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["redirect_uri"].ToString();

        Assert.StartsWith("https://", callbackUrl);
        Assert.Equal(redirectUri, callbackUrl);
    }

    [Fact]
    public async Task Status_requires_the_settings_permission()
    {
        using var factory = new ProjectWebApplicationFactory();
        using (var anonymous = ProjectWebApplicationFactory.CreateProjectClient(factory))
        using (var response = await anonymous.GetAsync("/api/v1/settings/external-login/status"))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var admin = await ProjectWebApplicationFactory.LoginAsync(factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var username = $"member_{Guid.NewGuid():N}"[..24];
        using (var create = await admin.Client.PostAsJsonAsync("/api/v1/users", new
        {
            Username = username,
            Email = $"{username}@example.test",
            Password = MemberPassword,
            IsActive = true
        }))
        {
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        }

        using var member = await ProjectWebApplicationFactory.LoginAsync(factory, username, MemberPassword);
        using var denied = await member.Client.GetAsync("/api/v1/settings/external-login/status");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
#if (IncludeMultiTenancy)

    /// <summary>租户管理员持有设置权限、通过了授权，仍因外部登录只属于宿主而被业务规则拒绝。</summary>
    [Fact]
    public async Task Status_from_a_tenant_is_rejected_as_host_only()
    {
        using var factory = new ProjectWebApplicationFactory();
        using var admin = await ProjectWebApplicationFactory.LoginAsync(factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var name = $"ext-{Guid.NewGuid():N}"[..16];
        Guid tenantId;
        using (var create = await admin.Client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = name,
            DisplayName = name,
            AdminEmail = $"admin@{name}.example.test",
            AdminPassword = TenantAdminPassword
        }))
        {
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
            tenantId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }

        using var tenantAdmin = ProjectWebApplicationFactory.CreateProjectClient(factory);
        tenantAdmin.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, tenantId.ToString());
        using (var login = await tenantAdmin.PostAsJsonAsync(
                   "/api/v1/auth/session-login",
                   new { UsernameOrEmail = "admin", Password = TenantAdminPassword }))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            tenantAdmin.DefaultRequestHeaders.Add("Cookie", string.Join("; ", login.Headers.GetValues("Set-Cookie")
                .Select(value => value.Split(';', 2)[0])));
        }

        using var response = await tenantAdmin.GetAsync("/api/v1/settings/external-login/status");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(AppSettingErrorCodes.ExternalLoginStatusHostOnly, body.RootElement.GetProperty("code").GetString());
    }
#endif

    private const string MemberPassword = "ExternalLoginSettings!Pw1";
#if (IncludeMultiTenancy)
    private const string TenantAdminPassword = "Tenant@123456";
#endif

    private static async Task EnableAsync(HttpClient client, string enabled, string clientId, string clientSecret)
    {
        await WriteAsync(client, clientId, "runtime-client");
        await WriteAsync(client, clientSecret, "runtime-secret");
        await WriteAsync(client, enabled, "true");
    }

    private static async Task<IReadOnlyList<string>> ProvidersAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/external-auth/providers"))
            .GetProperty("providers").EnumerateArray().Select(item => item.GetString()!).ToList();

    private static async Task<JsonElement> StatusAsync(HttpClient client, string provider) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/external-login/status"))
            .GetProperty("providers").EnumerateArray().Single(item => item.GetProperty("provider").GetString() == provider);

    private static async Task<string> ChallengeClientIdAsync(HttpClient client)
    {
        using var challenge = await client.GetAsync("/api/v1/external-auth/github/challenge");
        Assert.Equal(HttpStatusCode.Found, challenge.StatusCode);
        return QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["client_id"].ToString();
    }

    private static async Task WriteAsync(HttpClient client, string name, string? value)
    {
        using var response = await client.PutAsJsonAsync("/api/v1/settings/current-tenant", new { Name = name, Value = value });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
#endif
