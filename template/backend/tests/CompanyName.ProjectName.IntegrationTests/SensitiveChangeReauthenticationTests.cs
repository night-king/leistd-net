#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Domain.Users.Constants;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>
/// 改变"谁能登进这个账号"的操作要求近期认证：本人换邮箱，管理员改他人邮箱或标为已验证、重置他人口令。
/// 会话刚认证过时直接放行；开久了要当场出示操作者自己的当前口令，错了按再认证失败计数。
/// </summary>
public sealed class SensitiveChangeReauthenticationTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Password = "Reauth!Passw0rd";

    /// <summary>超出"刚认证过"的窗口。</summary>
    private static readonly TimeSpan StaleSessionAge = TimeSpan.FromMinutes(16);

    [Fact]
    public async Task Changing_your_email_from_an_older_session_requires_the_current_password()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var (username, _) = await CreateUserAsync(host, "reauth_email");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        clock.Advance(StaleSessionAge);
        var newEmail = $"new-{username}@example.test";

        using (var bare = await session.Client.PutAsJsonAsync("/api/v1/auth/me", new { Username = username, Email = newEmail }))
            Assert.Equal("Auth:ReauthenticationRequired", await ErrorCodeAsync(bare));
        using (var wrong = await session.Client.PutAsJsonAsync("/api/v1/auth/me",
                   new { Username = username, Email = newEmail, CurrentPassword = Password + "x" }))
            Assert.Equal("Security:CurrentPasswordIncorrect", await ErrorCodeAsync(wrong));
        Assert.Equal($"{username}@example.test", await EmailOfAsync(session.Client));

        using (var changed = await session.Client.PutAsJsonAsync("/api/v1/auth/me",
                   new { Username = username, Email = newEmail, CurrentPassword = Password }))
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(newEmail, await EmailOfAsync(session.Client));
    }

    // 只改用户名、显示名之类不涉及登录方式，开久了的会话照常能改；换邮箱只差大小写也不算换
    [Fact]
    public async Task Other_profile_changes_from_an_older_session_need_no_password()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var (username, _) = await CreateUserAsync(host, "reauth_name");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        clock.Advance(StaleSessionAge);

        using var changed = await session.Client.PutAsJsonAsync("/api/v1/auth/me",
            new { Username = username, Email = $"{username}@EXAMPLE.test", DisplayName = "Renamed" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
    }

    [Fact]
    public async Task A_recent_session_changes_the_email_without_a_password()
    {
        var (host, _) = CreateHost();
        using var _host = host;
        var (username, _) = await CreateUserAsync(host, "reauth_recent");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);

        using var changed = await session.Client.PutAsJsonAsync("/api/v1/auth/me",
            new { Username = username, Email = $"new-{username}@example.test" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
    }

    // 已验证的邮箱是按邮箱关联外部登录的依据：管理员改邮箱、标为已验证都要近期认证，只改显示名不用
    [Fact]
    public async Task An_older_admin_session_needs_its_password_to_change_or_verify_someone_elses_email()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var (username, userId) = await CreateUserAsync(host, "reauth_admin_mail");
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        clock.Advance(StaleSessionAge);
        var email = $"{username}@example.test";

        using (var renamed = await admin.Client.PutAsJsonAsync($"/api/v1/users/{userId}",
                   new { Email = email, DisplayName = "Renamed", IsEmailVerified = false }))
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        using (var changeBare = await admin.Client.PutAsJsonAsync($"/api/v1/users/{userId}",
                   new { Email = $"attacker-{email}", IsEmailVerified = false }))
            Assert.Equal("Auth:ReauthenticationRequired", await ErrorCodeAsync(changeBare));
        using (var verifyBare = await admin.Client.PutAsJsonAsync($"/api/v1/users/{userId}",
                   new { Email = email, IsEmailVerified = true }))
            Assert.Equal("Auth:ReauthenticationRequired", await ErrorCodeAsync(verifyBare));

        using (var verified = await admin.Client.PutAsJsonAsync($"/api/v1/users/{userId}",
                   new { Email = email, IsEmailVerified = true, Password = ProjectWebApplicationFactory.TestAdminPassword }))
            Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        var user = await admin.Client.GetFromJsonAsync<JsonElement>($"/api/v1/users/{userId}");
        Assert.True(user.GetProperty("isEmailVerified").GetBoolean());
    }

    [Fact]
    public async Task An_older_admin_session_needs_its_password_to_reset_someone_elses_password()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var (username, userId) = await CreateUserAsync(host, "reauth_admin_pwd");
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        clock.Advance(StaleSessionAge);
        const string newPassword = "Reauth!Passw0rd2";

        using (var bare = await admin.Client.PostAsJsonAsync($"/api/v1/users/{userId}/reset-password", new { Password = newPassword }))
            Assert.Equal("Auth:ReauthenticationRequired", await ErrorCodeAsync(bare));
        // 新口令不能顶替操作者自己的口令
        using (var wrong = await admin.Client.PostAsJsonAsync($"/api/v1/users/{userId}/reset-password",
                   new { Password = newPassword, CurrentPassword = newPassword }))
            Assert.Equal("Security:CurrentPasswordIncorrect", await ErrorCodeAsync(wrong));
        using (await ProjectWebApplicationFactory.LoginAsync(host, username, Password))
        {
        }

        using (var reset = await admin.Client.PostAsJsonAsync($"/api/v1/users/{userId}/reset-password",
                   new { Password = newPassword, CurrentPassword = ProjectWebApplicationFactory.TestAdminPassword }))
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        using (await ProjectWebApplicationFactory.LoginAsync(host, username, newPassword))
        {
        }
    }

    // 管理路由不给本人开后门：经用户管理改自己的邮箱，同样要近期认证
    [Fact]
    public async Task Changing_your_own_email_through_user_management_is_not_exempt()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var (username, userId) = await CreateUserAsync(host, "reauth_self_admin", isAdmin: true);
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);
        clock.Advance(StaleSessionAge);

        using var bare = await session.Client.PutAsJsonAsync($"/api/v1/users/{userId}",
            new { Email = $"new-{username}@example.test", IsEmailVerified = true });
        Assert.Equal("Auth:ReauthenticationRequired", await ErrorCodeAsync(bare));
    }

    private (WebApplicationFactory<Program> Host, FakeTimeProvider Clock) CreateHost()
    {
        // 从此刻起步：缓存条目按真实时钟回收，假时钟落在过去会让刚写入的条目被当成已过期
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
        return (host, clock);
    }

    private static async Task<(string Username, Guid Id)> CreateUserAsync(
        WebApplicationFactory<Program> host, string prefix, bool isAdmin = false)
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
        using var body = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();
        if (isAdmin)
        {
            Guid adminRoleId;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
                adminRoleId = await db.Roles.Where(role => role.Name == AdminConstant.RoleName).Select(role => role.Id).SingleAsync();
            }

            using var assign = await admin.Client.PutAsJsonAsync($"/api/v1/users/{id}/roles", new { RoleIds = new[] { adminRoleId } });
            Assert.True(assign.IsSuccessStatusCode, await assign.Content.ReadAsStringAsync());
        }

        return (username, id);
    }

    private static async Task<string?> EmailOfAsync(HttpClient client)
    {
        using var me = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        return me.RootElement.GetProperty("email").GetString();
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
#endif
