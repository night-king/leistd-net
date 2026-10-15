#if (LocalIdentity)
using Leistd.MultiTenancy.AspNetCore.Options;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Application.Permissions.Provider;
using Leistd.Authorization.Grants;
using Leistd.Authorization.Constants;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>宿主与租户的能力边界，以及宿主进入租户的正途（模拟登录）。</summary>
/// <remarks>
/// <para>这两组断言合在一处，因为它们是同一个判据的两面：宿主<b>看不见</b>租户的数据
/// （全局过滤器按 TenantId 分区），所以要在租户里做事只能<b>进到租户上下文</b>；
/// 反过来，宿主全局的资源（OpenIddict 客户端）租户<b>一条都不该碰得到</b>。</para>
/// <para>这类缺陷只在真的建了租户之后才暴露——单租户跑测试时两边恒等，永远是绿的。</para>
/// </remarks>
public sealed class ImpersonationTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>, IDisposable
{
    private const string TenantAdminPassword = "Tenant@123456";
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private async Task<AuthenticatedSession> LoginHostAdminAsync()
    {
        var session = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        _disposables.Add(session);
        return session;
    }

    private async Task<Guid> CreateTenantAsync(AuthenticatedSession hostAdmin, string name)
    {
        var response = await hostAdmin.Client.PostAsJsonAsync("/api/v1/tenants", new
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

    private async Task<HttpClient> LoginTenantAdminAsync(Guid tenantId)
    {
        var client = ProjectWebApplicationFactory.CreateProjectClient(factory);
        _disposables.Add(client);
        client.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, tenantId.ToString());

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/session-login",
            new { UsernameOrEmail = "admin", Password = TenantAdminPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = string.Join("; ", response.Headers.GetValues("Set-Cookie")
            .Select(value => value.Split(';', 2)[0]));
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static async Task<(string Id, string Name)> ReadIdentityAsync(HttpClient client)
    {
        using var body = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        var root = body.RootElement;
        var name = root.TryGetProperty("displayName", out var displayName) && displayName.GetString() is { Length: > 0 } value
            ? value
            : root.GetProperty("username").GetString()!;
        return (root.GetProperty("id").GetString()!, name);
    }

    private sealed record RecordRow(string Action, string TargetId, string? ActorId, string? ImpersonatorName);

    private static async Task<List<RecordRow>> ReadRecordsAsync(HttpClient client, string filter = "")
    {
        using var body = JsonDocument.Parse(
            await client.GetStringAsync($"/api/v1/operation-records?offset=0&limit=100{filter}"));
        return body.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => new RecordRow(
                item.GetProperty("action").GetString()!,
                item.GetProperty("targetId").GetString()!,
                item.TryGetProperty("actorId", out var actorId) ? actorId.GetString() : null,
                item.TryGetProperty("impersonatorName", out var impersonator) ? impersonator.GetString() : null))
            .ToList();
    }

    private static async Task<string> ReadUsernameAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("username").GetString()!;
    }

#if (OpenIddictServer)
    /// <summary>开放应用是宿主全局资源，租户管理员一条都碰不到。</summary>
    /// <remarks>
    /// OpenIddict 的表没有 TenantId，不是 <c>IMultiTenant</c>，全局租户过滤器对它们不生效——
    /// 因此这里没有"只看到自己那部分"这种中间态：要么全系统可见，要么一律拒绝。
    /// 边界由权限侧别（Host）单独承担：<c>DefaultPermissionChecker</c> 按当前侧别判定，
    /// 租户上下文下无论是否被授予都不通过。断言的是这个行为，不是某一层的实现。
    /// </remarks>
    [Fact]
    public async Task Tenant_admin_cannot_access_open_applications()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var tenantId = await CreateTenantAsync(hostAdmin, "openappedge");
        var tenantAdmin = await LoginTenantAdminAsync(tenantId);

        var response = await tenantAdmin.GetAsync("/api/v1/open-applications?offset=0&limit=20");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>租户的 Admin 角色不应被播种 <c>App.OpenApplications*</c>。</summary>
    /// <remarks>
    /// 播种按 <c>Side.HasFlag(MultiTenancySides.Tenant)</c> 过滤，因此侧别一旦漏标成默认的
    /// <c>Both</c>，这组权限会被授给每一个租户的管理员——而授予记录写进去之后不会自动撤销。
    /// 这条断言盯的是授予面，与上一条盯的执行面互补。
    /// </remarks>
    [Fact]
    public async Task Tenant_admin_role_has_no_open_application_permissions()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var tenantId = await CreateTenantAsync(hostAdmin, "grantedge");
        var tenantAdmin = await LoginTenantAdminAsync(tenantId);

        var response = await tenantAdmin.GetAsync("/api/v1/permissions/current");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("App.OpenApplications", payload, StringComparison.Ordinal);
    }
#endif

    /// <summary>宿主经模拟登录进入租户，再退回自己。</summary>
    /// <remarks>
    /// 已认证请求的租户由会话 Cookie 的 claim 定案（解析链首位的
    /// <c>CurrentPrincipalTenantResolveContributor</c> 对任何已认证主体都终止解析，
    /// 请求头改写不了），所以模拟登录必须<b>重新签发会话</b>，而不是加一个租户头。
    /// </remarks>
    [Fact]
    public async Task Host_can_impersonate_into_a_tenant_and_return()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var tenantId = await CreateTenantAsync(hostAdmin, "impersonated");
        var (_, hostAdminName) = await ReadIdentityAsync(hostAdmin.Client);

        // 进入：会话被换成租户管理员，且带上发起人声明
        var enter = await hostAdmin.Client.PostAsync($"/api/v1/tenants/{tenantId}/impersonate", null);
        Assert.Equal(HttpStatusCode.OK, enter.StatusCode);

        // 发起人原来那个会话随 Cookie 被换掉而结束：留着的话，设备列表里会多出一个再也用不上的会话，
        // 被复制走的旧 Cookie 也仍然有效
        Assert.Equal(HttpStatusCode.Unauthorized, (await hostAdmin.Client.GetAsync("/api/v1/auth/me")).StatusCode);

        var impersonatedCookie = string.Join("; ", enter.Headers.GetValues("Set-Cookie")
            .Select(value => value.Split(';', 2)[0]));
        using var impersonated = ProjectWebApplicationFactory.CreateProjectClient(factory);
        impersonated.DefaultRequestHeaders.Add("Cookie", impersonatedCookie);

        Assert.Equal("admin", await ReadUsernameAsync(impersonated));

        var status = await impersonated.GetFromJsonAsync<JsonElement>("/api/v1/auth/impersonation");
        Assert.True(status.GetProperty("isImpersonating").GetBoolean());
        // 顶栏与操作记录同一取法：发起人取显示名，租户取显示名（CreateTenantAsync 设为 "{name} Inc."）。
        Assert.Equal(hostAdminName, status.GetProperty("impersonatorName").GetString());
        Assert.Equal("impersonated Inc.", status.GetProperty("tenantName").GetString());

        // 模拟态下看到的是该租户的数据，而不是宿主的
        var users = await impersonated.GetAsync("/api/v1/users?offset=0&limit=50");
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);

        // 退出：回到发起人，且不再是模拟态
        var exit = await impersonated.PostAsync("/api/v1/auth/end-impersonation", null);
        Assert.Equal(HttpStatusCode.OK, exit.StatusCode);

        var restoredCookie = string.Join("; ", exit.Headers.GetValues("Set-Cookie")
            .Select(value => value.Split(';', 2)[0]));
        using var restored = ProjectWebApplicationFactory.CreateProjectClient(factory);
        restored.DefaultRequestHeaders.Add("Cookie", restoredCookie);

        var restoredStatus = await restored.GetFromJsonAsync<JsonElement>("/api/v1/auth/impersonation");
        Assert.False(restoredStatus.GetProperty("isImpersonating").GetBoolean());

        // 模拟会话同样随退出而结束
        Assert.Equal(HttpStatusCode.Unauthorized, (await impersonated.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    /// <summary>一次模拟在宿主与租户两侧各留开始、结束两条，且每条的操作人都是真实的那个人。</summary>
    /// <remarks>
    /// <para>两侧各答各的问题：宿主要知道"我们的人进了哪家"，租户要知道"谁以我的名义进来、什么时候走的"。
    /// 只记一侧时，另一侧的操作记录里这件事根本不存在。</para>
    /// <para>结束那一刻请求主体仍是被模拟的租户管理员，而两边管理员都叫 admin——
    /// 所以这里比对的是操作人 <b>Id</b>，只比名字看不出记到了谁头上。</para>
    /// <para>租户侧结束记录的模拟者名来自模拟声明；声明名与操作记录读取的不一致时它恒为空，
    /// 模拟期间的所有记录都会丢掉"由谁模拟操作"，而且不报错。</para>
    /// </remarks>
    // 进入再退出模拟只是两次会话切换，不是认证：认证时刻沿用发起人登录的那一刻。
    // 否则一个早已不新鲜的宿主管理员会话，绕一圈模拟就能免口令重置别人的两步验证
    [Fact]
    public async Task Returning_from_impersonation_does_not_make_an_old_admin_session_recent()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var tenantId = await CreateTenantAsync(admin, "stale" + Guid.NewGuid().ToString("N")[..8]);
        var targetId = await CreateHostUserAsync(admin.Client);
        clock.Advance(TimeSpan.FromMinutes(16));

        using (var stale = await admin.Client.PostAsJsonAsync($"/api/v1/users/{targetId}/reset-two-factor", new { }))
        {
            Assert.Equal("Auth:ReauthenticationRequired", await ErrorCodeAsync(stale));
        }

        using var enter = await admin.Client.PostAsync($"/api/v1/tenants/{tenantId}/impersonate", null);
        Assert.Equal(HttpStatusCode.OK, enter.StatusCode);
        using var impersonated = ProjectWebApplicationFactory.CreateProjectClient(host);
        impersonated.DefaultRequestHeaders.Add("Cookie", CookieOf(enter));
        using var exit = await impersonated.PostAsync("/api/v1/auth/end-impersonation", null);
        Assert.Equal(HttpStatusCode.OK, exit.StatusCode);
        using var restored = ProjectWebApplicationFactory.CreateProjectClient(host);
        restored.DefaultRequestHeaders.Add("Cookie", CookieOf(exit));

        using (var afterRoundTrip = await restored.PostAsJsonAsync($"/api/v1/users/{targetId}/reset-two-factor", new { }))
        {
            Assert.Equal("Auth:ReauthenticationRequired", await ErrorCodeAsync(afterRoundTrip));
        }

        using var withPassword = await restored.PostAsJsonAsync(
            $"/api/v1/users/{targetId}/reset-two-factor", new { Password = ProjectWebApplicationFactory.TestAdminPassword });
        Assert.Equal(HttpStatusCode.OK, withPassword.StatusCode);
    }

    private static async Task<Guid> CreateHostUserAsync(HttpClient admin)
    {
        var username = $"stale_{Guid.NewGuid():N}"[..20];
        using var create = await admin.PostAsJsonAsync("/api/v1/users", new
        {
            Username = username,
            Email = $"{username}@example.test",
            Password = TenantAdminPassword + "x",
            IsActive = true
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var users = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/users?offset=0&limit=10&keyword={username}");
        return users.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
    }

    private static string CookieOf(HttpResponseMessage response) =>
        string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';', 2)[0]));

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [Fact]
    public async Task Impersonation_records_start_and_end_on_both_sides()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var (hostAdminId, hostAdminName) = await ReadIdentityAsync(hostAdmin.Client);
        var tenantId = await CreateTenantAsync(hostAdmin, "auditpair");

        var enter = await hostAdmin.Client.PostAsync($"/api/v1/tenants/{tenantId}/impersonate", null);
        Assert.Equal(HttpStatusCode.OK, enter.StatusCode);
        using var impersonated = ProjectWebApplicationFactory.CreateProjectClient(factory);
        impersonated.DefaultRequestHeaders.Add("Cookie", string.Join("; ", enter.Headers.GetValues("Set-Cookie")
            .Select(value => value.Split(';', 2)[0])));
        var (tenantAdminId, _) = await ReadIdentityAsync(impersonated);

        var exit = await impersonated.PostAsync("/api/v1/auth/end-impersonation", null);
        Assert.Equal(HttpStatusCode.OK, exit.StatusCode);
        using var restored = ProjectWebApplicationFactory.CreateProjectClient(factory);
        restored.DefaultRequestHeaders.Add("Cookie", string.Join("; ", exit.Headers.GetValues("Set-Cookie")
            .Select(value => value.Split(';', 2)[0])));

        // 宿主记录跨用例共享（同一个夹具），只看本用例这个租户的。
        // 读取用退出后签发的会话：进入模拟时，原来那个会话已经结束
        var hostRecords = (await ReadRecordsAsync(restored))
            .Where(r => r.TargetId == tenantId.ToString() || r.Action.StartsWith("impersonation.", StringComparison.Ordinal))
            .ToList();
        var hostStarted = Assert.Single(hostRecords, r => r.Action == "tenant.impersonation-started");
        var hostEnded = Assert.Single(hostRecords, r => r.Action == "tenant.impersonation-ended");
        Assert.All([hostStarted, hostEnded], r => Assert.Equal(hostAdminId, r.ActorId));
        Assert.DoesNotContain(hostRecords, r => r.Action.StartsWith("impersonation.", StringComparison.Ordinal));

        var tenantRecords = await ReadRecordsAsync(await LoginTenantAdminAsync(tenantId));
        var tenantStarted = Assert.Single(tenantRecords, r => r.Action == "impersonation.started");
        Assert.Equal(hostAdminId, tenantStarted.ActorId);
        var tenantEnded = Assert.Single(tenantRecords, r => r.Action == "impersonation.ended");
        Assert.Equal(tenantAdminId, tenantEnded.ActorId);
        Assert.Equal(hostAdminName, tenantEnded.ImpersonatorName);
        Assert.DoesNotContain(tenantRecords, r => r.Action.StartsWith("tenant.impersonation", StringComparison.Ordinal));
    }

    /// <summary>操作记录的类别与动作同时筛选时取交集。</summary>
    /// <remarks>
    /// 放在这里是因为模拟登录的宿主侧记录与"创建租户"同属租户类，正好凑出同一类别下的两个动作。
    /// 取并集时，选了类别再选动作一条都不会少——界面上的动作候选随类别联动，就是在类别之内收窄。
    /// </remarks>
    [Fact]
    public async Task Operation_record_category_and_action_filters_intersect()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var tenantId = await CreateTenantAsync(hostAdmin, "filterand");
        var enter = await hostAdmin.Client.PostAsync($"/api/v1/tenants/{tenantId}/impersonate", null);
        Assert.Equal(HttpStatusCode.OK, enter.StatusCode);

        // 进入模拟后原会话已结束，另开一个宿主会话来读
        var reader = await LoginHostAdminAsync();
        var categoryOnly = await ReadRecordsAsync(reader.Client, "&categories=tenant");
        Assert.Contains(categoryOnly, r => r.Action == "tenant.created");

        var both = await ReadRecordsAsync(
            reader.Client, "&categories=tenant&actions=tenant.impersonation-started");
        Assert.NotEmpty(both);
        Assert.All(both, r => Assert.Equal("tenant.impersonation-started", r.Action));
    }

    /// <summary>租户上下文内不得发起模拟登录。</summary>
    /// <remarks>
    /// 用例先把这条宿主侧权限<b>强行授予</b>租户内的主体，再断言仍被拒——
    /// 证明侧别检查与"有没有被授予"无关：手工写进库的一条授予记录不会变成越权。
    /// 这也是不必在应用服务里重复一遍宿主判断的依据。
    /// </remarks>
    [Fact]
    public async Task Impersonation_cannot_start_in_a_tenant_context()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var tenantId = await CreateTenantAsync(hostAdmin, "nonested");
        var tenantAdmin = await LoginTenantAdminAsync(tenantId);

        // 把宿主侧权限直接授给租户内的主体：证明侧别不是靠"没授予"生效的
        using (var scope = factory.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IPermissionGrantManager>();
            var me = await tenantAdmin.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
            await manager.GrantAsync(
                PermissionConstant.Tenants.Impersonation,
                PermissionGrantProviderNames.User,
                me.GetProperty("id").GetGuid().ToString());
        }

        var response = await tenantAdmin.PostAsync($"/api/v1/tenants/{tenantId}/impersonate", null);

        Assert.True(
            response.StatusCode is HttpStatusCode.Forbidden,
            $"Impersonation from a tenant context must be rejected, actual {(int)response.StatusCode}");
    }
}
#endif
