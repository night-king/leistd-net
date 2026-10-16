#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Application.Auth.Sessions;
using CompanyName.ProjectName.Application.Settings.Provider;
using CompanyName.ProjectName.Infrastructure.Persistence;
#if (IncludeMultiTenancy)
using CompanyName.ProjectName.Application.Auth.BackgroundJobs;
using Leistd.BackgroundJobs.Recurring;
using Leistd.MultiTenancy.AspNetCore.Options;
#endif
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>
/// 会话空闲时限是租户策略：签发的会话 Cookie 按它定有效期；调短后空闲已超过新时长的会话下一次请求即失效，
/// 调长后续发的 Cookie 跟上新时长。
/// </summary>
public sealed class SessionIdleTimeoutTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromHours(1);

    [Fact]
    public async Task A_new_session_cookie_lasts_as_long_as_the_tenant_policy()
    {
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        await SetIdleDaysAsync(admin.Client, "3");
        try
        {
            using var client = ProjectWebApplicationFactory.CreateProjectClient(factory);
            using var login = await client.PostAsJsonAsync("/api/v1/auth/session-login",
                new { UsernameOrEmail = "admin", Password = ProjectWebApplicationFactory.TestAdminPassword });

            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            AssertExpiresIn(login, TimeSpan.FromDays(3));
        }
        finally
        {
            await SetIdleDaysAsync(admin.Client, null);
        }
    }

    [Fact]
    public async Task Shortening_the_limit_ends_sessions_already_idle_longer()
    {
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var idle = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var sessionId = await CurrentSessionIdAsync(idle.Client);
        await AgeSessionAsync(sessionId, TimeSpan.FromDays(2));

        // 默认 7 天：空闲两天的会话仍有效
        await ForgetValidationAsync(sessionId);
        Assert.Equal(HttpStatusCode.OK, (await idle.Client.GetAsync("/api/v1/auth/me")).StatusCode);

        await SetIdleDaysAsync(admin.Client, "1");
        try
        {
            await AgeSessionAsync(sessionId, TimeSpan.FromDays(2));
            await ForgetValidationAsync(sessionId);
            Assert.Equal(HttpStatusCode.Unauthorized, (await idle.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        }
        finally
        {
            await SetIdleDaysAsync(admin.Client, null);
        }
    }

    [Fact]
    public async Task Lengthening_the_limit_renews_the_cookie_with_the_new_lifetime()
    {
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var session = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var sessionId = await CurrentSessionIdAsync(session.Client);

        await SetIdleDaysAsync(admin.Client, "30");
        try
        {
            await ForgetValidationAsync(sessionId);
            using var renewed = await session.Client.GetAsync("/api/v1/auth/me");

            Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
            AssertExpiresIn(renewed, TimeSpan.FromDays(30));

            // 跨度已等于策略时不再续发
            await ForgetValidationAsync(sessionId);
            using var client = ProjectWebApplicationFactory.CreateProjectClient(factory);
            client.DefaultRequestHeaders.Add("Cookie", CookieOf(renewed));
            using var settled = await client.GetAsync("/api/v1/auth/me");
            Assert.Equal(HttpStatusCode.OK, settled.StatusCode);
            Assert.False(settled.Headers.Contains("Set-Cookie"));
        }
        finally
        {
            await SetIdleDaysAsync(admin.Client, null);
        }
    }
#if (IncludeMultiTenancy)

    // 宿主与租户各自的时长互不影响：租户会话按租户的值签发；过期清理在同一个共享库里按各自的时长删
    [Fact]
    public async Task Hosts_and_tenants_keep_their_own_idle_limits()
    {
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var tenantId = await CreateTenantAsync(admin.Client);
        using var tenantAdmin = await LoginTenantAdminAsync(tenantId);

        await SetIdleDaysAsync(admin.Client, "3");
        await SetIdleDaysAsync(tenantAdmin.Client, "30");
        try
        {
            using var tenantClient = ProjectWebApplicationFactory.CreateProjectClient(factory);
            tenantClient.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, tenantId.ToString());
            using var login = await tenantClient.PostAsJsonAsync("/api/v1/auth/session-login",
                new { UsernameOrEmail = "admin", Password = TenantAdminPassword });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            AssertExpiresIn(login, TimeSpan.FromDays(30));

            // 都空闲了 10 天：宿主（3 天）的该删，租户（30 天）的该留
            var hostSession = await CurrentSessionIdAsync(admin.Client);
            var tenantSession = await CurrentSessionIdAsync(tenantAdmin.Client);
            await AgeSessionAsync(hostSession, TimeSpan.FromDays(10));
            await AgeSessionAsync(tenantSession, TimeSpan.FromDays(10));

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ExpiredUserSessionCleanupJob>().ExecuteAsync(
                    new RecurringJobContext(ExpiredUserSessionCleanupJob.Name, DateTimeOffset.UtcNow), CancellationToken.None);
            }

            Assert.False(await SessionExistsAsync(hostSession));
            Assert.True(await SessionExistsAsync(tenantSession));
        }
        finally
        {
            await SetIdleDaysAsync(tenantAdmin.Client, null);
            using var restorer = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
            await SetIdleDaysAsync(restorer.Client, null);
        }
    }
#endif
#if (Impersonation)

    // 进入模拟时环境是宿主、会话属于目标租户；结束时反过来。Cookie 的时长都按会话所属的那一边
    [Fact]
    public async Task Impersonation_cookies_follow_the_policy_of_the_tenant_they_belong_to()
    {
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var name = $"idle-{Guid.NewGuid():N}"[..16];
        Guid tenantId;
        using (var create = await admin.Client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = name,
            DisplayName = name,
            AdminEmail = $"admin@{name}.example.test",
            AdminPassword = "Tenant@123456"
        }))
        {
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
            tenantId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }

        await SetIdleDaysAsync(admin.Client, "3");
        try
        {
            // 进入模拟会换掉发起者的会话，另开一个，留着 admin 收尾恢复设置
            using var impersonator = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
            using var enter = await impersonator.Client.PostAsync($"/api/v1/tenants/{tenantId}/impersonate", null);
            Assert.Equal(HttpStatusCode.OK, enter.StatusCode);
            AssertExpiresIn(enter, TimeSpan.FromDays(7));

            using var impersonated = ProjectWebApplicationFactory.CreateProjectClient(factory);
            impersonated.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, tenantId.ToString());
            impersonated.DefaultRequestHeaders.Add("Cookie", CookieOf(enter));
            using var exit = await impersonated.PostAsync("/api/v1/auth/end-impersonation", null);
            Assert.Equal(HttpStatusCode.OK, exit.StatusCode);
            AssertExpiresIn(exit, TimeSpan.FromDays(3));
        }
        finally
        {
            await SetIdleDaysAsync(admin.Client, null);
        }
    }
#endif

#if (IncludeMultiTenancy)
    private const string TenantAdminPassword = "Tenant@123456";

    private static async Task<Guid> CreateTenantAsync(HttpClient hostAdmin)
    {
        var name = $"idle-{Guid.NewGuid():N}"[..16];
        using var create = await hostAdmin.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = name,
            DisplayName = name,
            AdminEmail = $"admin@{name}.example.test",
            AdminPassword = TenantAdminPassword
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        return (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<AuthenticatedSession> LoginTenantAdminAsync(Guid tenantId)
    {
        var client = ProjectWebApplicationFactory.CreateProjectClient(factory);
        client.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, tenantId.ToString());
        using var login = await client.PostAsJsonAsync("/api/v1/auth/session-login",
            new { UsernameOrEmail = "admin", Password = TenantAdminPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = CookieOf(login);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return new AuthenticatedSession(client, cookie);
    }

    private async Task<bool> SessionExistsAsync(Guid sessionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MyProjectDbContext>().UserSessions
            .IgnoreQueryFilters().AnyAsync(session => session.Id == sessionId);
    }
#endif

    private static async Task SetIdleDaysAsync(HttpClient client, string? days)
    {
        using var response = await client.PutAsJsonAsync(
            "/api/v1/settings/current-tenant",
            new { Name = SettingConstant.Security.SessionIdleTimeoutDays, Value = days });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<Guid> CurrentSessionIdAsync(HttpClient client)
        => (await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/sessions"))
            .EnumerateArray()
            .Single(session => session.GetProperty("isCurrent").GetBoolean())
            .GetProperty("id").GetGuid();

    // 登录流程造不出空闲多日的会话，直接改最近活动时间
    private async Task AgeSessionAsync(Guid sessionId, TimeSpan idleFor)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var lastSeen = DateTime.UtcNow - idleFor;
        await scope.ServiceProvider.GetRequiredService<MyProjectDbContext>().UserSessions
            .IgnoreQueryFilters()
            .Where(session => session.Id == sessionId)
            .ExecuteUpdateAsync(update => update.SetProperty(session => session.LastSeenTime, lastSeen));
    }

    // 有效结果按分钟缓存；测试里清掉它，让下一次请求实际查会话、读策略
    private async Task ForgetValidationAsync(Guid sessionId)
        => await factory.Services.GetRequiredService<IDistributedCache>()
            .RemoveAsync(UserSessionValidator.CacheKey(sessionId));

    private static void AssertExpiresIn(HttpResponseMessage response, TimeSpan lifetime)
    {
        var cookie = SetCookieHeaderValue.ParseList(response.Headers.GetValues("Set-Cookie").ToList())
            .Single(value => value.Name.Value!.EndsWith(".Auth", StringComparison.Ordinal) && value.Expires is not null);
        var expected = DateTimeOffset.UtcNow + lifetime;
        Assert.InRange(cookie.Expires!.Value, expected - Tolerance, expected + Tolerance);
    }

    private static string CookieOf(HttpResponseMessage response)
        => string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';', 2)[0]));
}
#endif
