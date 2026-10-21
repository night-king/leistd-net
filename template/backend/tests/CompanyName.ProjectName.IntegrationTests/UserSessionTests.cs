#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Application.Auth.BackgroundJobs;
using CompanyName.ProjectName.Domain.Auth.Entities;
using CompanyName.ProjectName.Domain.Auth.Options;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.BackgroundJobs.Options;
using Leistd.BackgroundJobs.Recurring;
using Leistd.Ddd.Domain.Repositories;
using Leistd.UnitOfWork;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using CompanyName.ProjectName.Application.Shared;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>登录设备：每次登录登记一个会话，撤销对已发出的 Cookie 立即生效。</summary>
/// <remarks>
/// 断言都落在"那份 Cookie 还能不能用"上：会话表里少一行不算数，Cookie 仍被放行才是要防的事。
/// </remarks>
public sealed class UserSessionTests(ProjectWebApplicationFactory factory) : IClassFixture<ProjectWebApplicationFactory>
{
    [Fact]
    public async Task Deployed_session_cookie_carries_the_host_http_prefix_and_its_attributes()
    {
        using var client = ProjectWebApplicationFactory.CreateProjectClient(factory);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/session-login",
            new { UsernameOrEmail = "admin", Password = ProjectWebApplicationFactory.TestAdminPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ProjectWebApplicationFactory.AssertSessionCookieContract(response);
    }

    [Fact]
    public void Development_keeps_the_unprefixed_session_cookie_for_http_debugging()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AuthenticationSchemeNames.SessionCookie);
        Assert.Equal("CompanyName.ProjectName.Auth", options.Cookie.Name);
        Assert.Equal(CookieSecurePolicy.SameAsRequest, options.Cookie.SecurePolicy);
    }

    private const string Password = "SessionTests!Passw0rd";

    [Fact]
    public async Task Each_sign_in_registers_a_session_with_the_current_one_first()
    {
        var username = await CreateUserAsync("sess_list");
        using var first = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);
        using var second = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);

        var sessions = await ReadSessionsAsync(second.Client);

        Assert.Equal(2, sessions.Count);
        Assert.True(sessions[0].GetProperty("isCurrent").GetBoolean());
        Assert.False(sessions[1].GetProperty("isCurrent").GetBoolean());
    }

    [Fact]
    public async Task Revoked_device_cookie_stops_working_immediately()
    {
        var username = await CreateUserAsync("sess_one");
        using var mine = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);
        using var other = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);
        var otherId = (await ReadSessionsAsync(other.Client)).Single(s => s.GetProperty("isCurrent").GetBoolean())
            .GetProperty("id").GetGuid();

        using var revoke = await mine.Client.DeleteAsync($"/api/v1/auth/me/sessions/{otherId}");

        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mine.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Revoke_endpoint_cannot_end_the_current_session()
    {
        var username = await CreateUserAsync("sess_self");
        using var mine = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);
        var currentId = (await ReadSessionsAsync(mine.Client)).Single().GetProperty("id").GetGuid();

        using var revoke = await mine.Client.DeleteAsync($"/api/v1/auth/me/sessions/{currentId}");

        Assert.Equal(HttpStatusCode.Conflict, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mine.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Cannot_revoke_another_users_session()
    {
        var victim = await CreateUserAsync("sess_victim");
        using var victimSession = await ProjectWebApplicationFactory.LoginAsync(factory, victim, Password);
        var victimSessionId = (await ReadSessionsAsync(victimSession.Client)).Single().GetProperty("id").GetGuid();

        var attacker = await CreateUserAsync("sess_attacker");
        using var attackerSession = await ProjectWebApplicationFactory.LoginAsync(factory, attacker, Password);
        using var revoke = await attackerSession.Client.DeleteAsync($"/api/v1/auth/me/sessions/{victimSessionId}");

        // 静默成功：不借此透露别人的会话 Id 是否存在；但对方的会话必须还在
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await victimSession.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Signing_out_other_devices_keeps_only_the_current_session()
    {
        var username = await CreateUserAsync("sess_others");
        using var a = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);
        using var b = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);
        using var mine = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);

        using var revoke = await mine.Client.PostAsync("/api/v1/auth/me/sessions/revoke-others", null);

        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Single(await ReadSessionsAsync(mine.Client));
    }

    [Fact]
    public async Task Password_change_signs_out_other_devices()
    {
        var username = await CreateUserAsync("sess_pwd");
        using var other = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);
        using var mine = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);

        using var change = await mine.Client.PostAsJsonAsync("/api/v1/auth/change-password", new
        {
            CurrentPassword = Password,
            NewPassword = Password + "2",
            ConfirmPassword = Password + "2"
        });

        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mine.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Admin_password_reset_ends_all_user_sessions()
    {
        var username = await CreateUserAsync("sess_reset");
        using var target = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);
        var targetId = await ReadUserIdAsync(target.Client);

        using var admin = await ProjectWebApplicationFactory.LoginAsync(factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var reset = await admin.Client.PostAsJsonAsync($"/api/v1/users/{targetId}/reset-password", new { Password = Password + "3" });

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await target.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Copied_cookie_is_rejected_after_sign_out()
    {
        var username = await CreateUserAsync("sess_logout");
        using var mine = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);

        // 退出前复制一份 Cookie：只删浏览器里的 Cookie 挡不住这份副本
        using var copy = ProjectWebApplicationFactory.CreateProjectClient(factory);
        copy.DefaultRequestHeaders.Add("Cookie", mine.Cookie);

        using var logout = await mine.Client.PostAsync("/api/v1/auth/logout", null);

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await copy.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    /// <summary>"退出其他设备"只把仍然有效的设备算进返回值，已过期的会话一并清掉但不计数。</summary>
    [Fact]
    public async Task Signing_out_other_devices_counts_only_active_devices()
    {
        var username = await CreateUserAsync("sess_count");
        using var other = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);
        using var mine = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);
        var userId = await ReadUserIdAsync(mine.Client);
        var expiredId = await InsertExpiredSessionAsync(userId);

        using var revoke = await mine.Client.PostAsync("/api/v1/auth/me/sessions/revoke-others", null);

        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(1, await revoke.Content.ReadFromJsonAsync<int>());
        Assert.False(await SessionExistsAsync(expiredId));
    }

    /// <summary>不再登录的用户，过期会话（连同原始 IP）由每日清理作业删掉，有效会话不受影响。</summary>
    [Fact]
    public async Task The_cleanup_job_deletes_expired_sessions_of_users_who_never_sign_in_again()
    {
        var username = await CreateUserAsync("sess_cleanup");
        using var active = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);
        var expiredId = await InsertExpiredSessionAsync(await ReadUserIdAsync(active.Client));

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ExpiredUserSessionCleanupJob>().ExecuteAsync(
                new RecurringJobContext(ExpiredUserSessionCleanupJob.Name, DateTimeOffset.UtcNow), CancellationToken.None);
        }

        Assert.False(await SessionExistsAsync(expiredId));
        Assert.Equal(HttpStatusCode.OK, (await active.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Session_cleanup_matches_the_domain_rule_at_the_exact_idle_cutoff()
    {
        // 微秒可表示的 UTC 起点；只建立一个配置变体，共用 fixture 的 PostgreSQL。
        var ticks = DateTimeOffset.UtcNow.UtcTicks;
        var clock = new FakeTimeProvider(new DateTimeOffset(ticks - ticks % 10, TimeSpan.Zero));
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.PostConfigure<BackgroundJobOptions>(options => options.Enabled = false);
        }));
        Guid[] ids;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            var userId = await db.Users.Select(user => user.Id).FirstAsync();
            var timeout = scope.ServiceProvider.GetRequiredService<IOptions<UserSessionOptions>>().Value.IdleTimeout;
            var now = clock.GetUtcNow().UtcDateTime;
            var cutoff = now - timeout;
            var before = new UserSession(userId, cutoff.AddMilliseconds(-1), "203.0.113.10", "before cutoff");
            var equal = new UserSession(userId, cutoff, "203.0.113.11", "at cutoff");
            var after = new UserSession(userId, cutoff.AddMilliseconds(1), "203.0.113.12", "after cutoff");
            Assert.True(before.IsExpired(now, timeout));
            Assert.True(equal.IsExpired(now, timeout));
            Assert.False(after.IsExpired(now, timeout));
            db.UserSessions.AddRange(before, equal, after);
            await db.SaveChangesAsync();
            ids = [before.Id, equal.Id, after.Id];

            await scope.ServiceProvider.GetRequiredService<ExpiredUserSessionCleanupJob>().ExecuteAsync(
                new RecurringJobContext(ExpiredUserSessionCleanupJob.Name, clock.GetUtcNow()), CancellationToken.None);
        }

        // 批量删除绕过变更跟踪；新 scope 读取真实 SQL 结果，不能拿旧实体当证据。
        await using var verify = host.Services.CreateAsyncScope();
        var remaining = await verify.ServiceProvider.GetRequiredService<MyProjectDbContext>().UserSessions
            .IgnoreQueryFilters().Where(session => ids.Contains(session.Id)).Select(session => session.Id).ToArrayAsync();
        Assert.Equal(ids[2], Assert.Single(remaining));
    }

    // 最近活动在空闲超时之前的会话：登录流程不会造出这样的行，直接写库
    private async Task<Guid> InsertExpiredSessionAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var idleTimeout = scope.ServiceProvider.GetRequiredService<IOptions<UserSessionOptions>>().Value.IdleTimeout;
        using var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true);
        var session = await scope.ServiceProvider.GetRequiredService<IRepository<UserSession, Guid>>().InsertAsync(
            new UserSession(userId, DateTime.UtcNow - idleTimeout - TimeSpan.FromDays(1), "203.0.113.9", "stale device"));
        await unitOfWork.CompleteAsync();
        return session.Id;
    }

    private async Task<bool> SessionExistsAsync(Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        using var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true);
        return await scope.ServiceProvider.GetRequiredService<IRepository<UserSession, Guid>>().GetByIdAsync(sessionId) is not null;
    }

    private async Task<string> CreateUserAsync(string prefix)
    {
        var username = $"{prefix}_{Guid.NewGuid():N}"[..30];
        using var admin = await ProjectWebApplicationFactory.LoginAsync(factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);
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

    private static async Task<List<JsonElement>> ReadSessionsAsync(HttpClient client)
    {
        using var body = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me/sessions"));
        return body.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static async Task<Guid> ReadUserIdAsync(HttpClient client)
    {
        using var me = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        return me.RootElement.GetProperty("id").GetGuid();
    }
}
#endif
