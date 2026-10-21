#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Application.Settings.Provider;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>登录失败锁定：按租户设置的阈值锁定、锁定期间不校验密码、已登录会话不受影响、管理员可提前解除。</summary>
/// <remarks>用例在同一个夹具里顺序执行；改设置的用例在结束时清掉覆盖值，回到默认的 5 次 / 15 分钟。</remarks>
public sealed class LoginLockoutTests(ProjectWebApplicationFactory factory) : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Password = "LockoutTests!Passw0rd";
    private const string WrongPassword = "LockoutTests!Wrong000";

    [Fact]
    public async Task Locks_out_at_the_threshold_even_for_the_correct_password()
    {
        var username = await CreateUserAsync("lock_basic");

        for (var i = 1; i < SettingConstant.Security.DefaultLockoutMaxFailedAttempts; i++)
        {
            Assert.Equal("Auth:InvalidCredentials", await LoginErrorCodeAsync(username, WrongPassword));
        }

        // 触发锁定的那一次就告知已锁定，而不是再报一次"密码错误"
        Assert.Equal("Auth:UserTemporarilyLockedOut", await LoginErrorCodeAsync(username, WrongPassword));
        // 锁定期间不看密码：正确密码得到的也是同一个结果，试不出哪个是对的
        Assert.Equal("Auth:UserTemporarilyLockedOut", await LoginErrorCodeAsync(username, Password));
    }

    [Fact]
    public async Task Existing_sessions_are_not_affected_by_lockout()
    {
        var username = await CreateUserAsync("lock_session");
        using var signedIn = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);

        await LockOutAsync(username);

        Assert.Equal(HttpStatusCode.OK, (await signedIn.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Admin_unlock_allows_sign_in_and_clears_the_lockout_flag()
    {
        var username = await CreateUserAsync("lock_unlock");
        await LockOutAsync(username);

        using var admin = await ProjectWebApplicationFactory.LoginAsync(factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var locked = await FindUserAsync(admin.Client, username);
        Assert.True(locked.GetProperty("isLockedOut").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, locked.GetProperty("lockoutEnd").ValueKind);

        using var unlock = await admin.Client.PostAsync($"/api/v1/users/{locked.GetProperty("id").GetGuid()}/unlock", null);
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);

        Assert.False((await FindUserAsync(admin.Client, username)).GetProperty("isLockedOut").GetBoolean());
        using var session = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Zero_threshold_disables_lockout()
    {
        using var admin = await ProjectWebApplicationFactory.LoginAsync(factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        await WriteSettingAsync(admin.Client, SettingConstant.Security.LockoutMaxFailedAttempts, "0");
        try
        {
            var username = await CreateUserAsync("lock_off");
            for (var i = 0; i < SettingConstant.Security.DefaultLockoutMaxFailedAttempts * 2; i++)
            {
                Assert.Equal("Auth:InvalidCredentials", await LoginErrorCodeAsync(username, WrongPassword));
            }

            using var session = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);
        }
        finally
        {
            await WriteSettingAsync(admin.Client, SettingConstant.Security.LockoutMaxFailedAttempts, null);
        }
    }

    [Fact]
    public async Task Rejects_a_threshold_outside_the_allowed_range()
    {
        using var admin = await ProjectWebApplicationFactory.LoginAsync(factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);

        using var rejected = await admin.Client.PutAsJsonAsync(
            "/api/v1/settings/current-tenant",
            new { Name = SettingConstant.Security.LockoutDurationMinutes, Value = "0" });

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    private async Task LockOutAsync(string username)
    {
        for (var i = 0; i < SettingConstant.Security.DefaultLockoutMaxFailedAttempts; i++)
        {
            await LoginErrorCodeAsync(username, WrongPassword);
        }

        Assert.Equal("Auth:UserTemporarilyLockedOut", await LoginErrorCodeAsync(username, Password));
    }

    private async Task<string?> LoginErrorCodeAsync(string username, string password)
    {
        using var client = ProjectWebApplicationFactory.CreateProjectClient(factory);
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/session-login",
            new { UsernameOrEmail = username, Password = password });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
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

    private static async Task<JsonElement> FindUserAsync(HttpClient client, string username)
    {
        using var body = JsonDocument.Parse(
            await client.GetStringAsync($"/api/v1/users?offset=0&limit=10&keyword={username}"));
        return body.RootElement.GetProperty("items").EnumerateArray()
            .Single(u => u.GetProperty("username").GetString() == username)
            .Clone();
    }

    private static async Task WriteSettingAsync(HttpClient client, string name, string? value)
    {
        using var response = await client.PutAsJsonAsync(
            "/api/v1/settings/current-tenant", new { Name = name, Value = value });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
#endif
