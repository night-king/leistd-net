#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>
/// 用通行密钥确认身份：开久了的会话做敏感操作时出示通行密钥断言即可放行；选项按本人与会话签发、只认一次，
/// 校验不通过按再认证失败计数。只有通行密钥的人也能凭它重新生成恢复码。
/// </summary>
public sealed class PasskeyReauthenticationTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Password = "PasskeyReauth!Passw0rd";
    private static readonly TimeSpan StaleSessionAge = TimeSpan.FromMinutes(16);

    [Fact]
    public async Task An_older_session_confirms_a_sensitive_change_with_a_passkey()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var (username, session, passkey) = await CreateUserWithPasskeyAsync(host, "pkr_email");
        clock.Advance(StaleSessionAge);
        var newEmail = $"new-{username}@example.test";

        using (var bare = await session.PutAsJsonAsync("/api/v1/auth/me", new { Username = username, Email = newEmail }))
        {
            Assert.Equal("Auth:ReauthenticationRequired", await ErrorCodeAsync(bare));
        }

        // 没取过选项就交来的断言：仪式不在，不算猜错
        using (var early = await session.PutAsJsonAsync("/api/v1/auth/me", new { Username = username, Email = newEmail, Passkey = new { id = "x" } }))
        {
            Assert.Equal("Auth:PasskeyCeremonyExpired", await ErrorCodeAsync(early));
        }

        var options = await ReauthOptionsAsync(session);
        using (var changed = await session.PutAsJsonAsync("/api/v1/auth/me", new { Username = username, Email = newEmail, Passkey = passkey.Sign(options) }))
        {
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        }

        // 同一份选项只认一次
        using var replay = await session.PutAsJsonAsync("/api/v1/auth/me", new { Username = username, Email = $"again-{username}@example.test", Passkey = passkey.Sign(options) });
        Assert.Equal("Auth:PasskeyCeremonyExpired", await ErrorCodeAsync(replay));
    }

    [Fact]
    public async Task A_failed_passkey_confirmation_counts_as_a_reauthentication_failure()
    {
        var (host, clock) = CreateHost();
        using var _ = host;
        var (username, session, passkey) = await CreateUserWithPasskeyAsync(host, "pkr_failed");
        clock.Advance(StaleSessionAge);

        var options = await ReauthOptionsAsync(session);
        using var rejected = await session.PutAsJsonAsync("/api/v1/auth/me", new
        {
            Username = username,
            Email = $"new-{username}@example.test",
            Passkey = passkey.Sign(options, origin: "https://evil.example")
        });

        Assert.Equal("Auth:PasskeyVerificationFailed", await ErrorCodeAsync(rejected));
    }

    [Fact]
    public async Task A_passkey_only_account_regenerates_its_recovery_codes_with_the_passkey()
    {
        var (host, _) = CreateHost();
        using var __ = host;
        var (_, session, passkey) = await CreateUserWithPasskeyAsync(host, "pkr_codes");

        using (var noCode = await session.PostAsJsonAsync("/api/v1/auth/me/two-factor/recovery-codes", new { }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noCode.StatusCode);
        }

        var options = await ReauthOptionsAsync(session);
        using var regenerated = await session.PostAsJsonAsync("/api/v1/auth/me/two-factor/recovery-codes", new { Passkey = passkey.Sign(options) });
        Assert.Equal(HttpStatusCode.OK, regenerated.StatusCode);
        Assert.Equal(10, (await regenerated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes").GetArrayLength());
    }

    // 校验、选分支与转成证明用同一个判据：非对象的值若在校验里算"没给"、在分支里算"给了"，
    // 就成了一份空证明，近期会话随之放行，不经任何独立因子换出一组新恢复码
    [Theory]
    [InlineData("[]")]
    [InlineData("\"assertion\"")]
    [InlineData("1")]
    [InlineData("true")]
    public async Task A_malformed_passkey_does_not_regenerate_recovery_codes(string passkey)
    {
        var (host, _) = CreateHost();
        using var __ = host;
        var (_, session, _) = await CreateUserWithPasskeyAsync(host, "pkr_shape");

        using var content = new StringContent($$"""{"code":"000000","passkey":{{passkey}}}""", System.Text.Encoding.UTF8, "application/json");
        using var response = await session.PostAsync("/api/v1/auth/me/two-factor/recovery-codes", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var alone = new StringContent($$"""{"passkey":{{passkey}}}""", System.Text.Encoding.UTF8, "application/json");
        using var aloneResponse = await session.PostAsync("/api/v1/auth/me/two-factor/recovery-codes", alone);
        Assert.Equal(HttpStatusCode.BadRequest, aloneResponse.StatusCode);
    }

    private (WebApplicationFactory<Program> Host, FakeTimeProvider Clock) CreateHost()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
        return (host, clock);
    }

    private static async Task<(string Username, HttpClient Session, SoftwarePasskey Passkey)> CreateUserWithPasskeyAsync(
        WebApplicationFactory<Program> host, string prefix)
    {
        var username = $"{prefix}_{Guid.NewGuid():N}"[..30];
        using (var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword))
        using (var create = await admin.Client.PostAsJsonAsync("/api/v1/users", new
        {
            Username = username,
            Email = $"{username}@example.test",
            Password,
            IsActive = true,
            IsEmailVerified = true
        }))
        {
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        }

        var session = (await ProjectWebApplicationFactory.LoginAsync(host, username, Password)).Client;
        var (_, passkey) = await PasskeyRequests.RegisterAsync(session);
        return (username, session, passkey);
    }

    private static async Task<JsonElement> ReauthOptionsAsync(HttpClient session)
    {
        using var response = await session.PostAsJsonAsync("/api/v1/auth/me/reauthentication/passkey-options", new { });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("options");
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        Assert.False(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
#endif
