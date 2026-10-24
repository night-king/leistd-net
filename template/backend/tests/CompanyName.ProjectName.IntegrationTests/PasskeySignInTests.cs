#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompanyName.ProjectName.Application.Settings.Provider;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>
/// 用通行密钥登录：免口令登录直接签发会话、满足组织要求；口令登录之后作为第二步的默认方式。
/// 断言经真实协议校验：仪式只认一次、来源与计数器不对不认，认出账号后的失败计入登录失败与锁定。
/// </summary>
public sealed class PasskeySignInTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Password = "PasskeySignIn!Passw0rd";

    [Fact]
    public async Task A_passkey_signs_in_without_a_password_or_a_second_step()
    {
        var (username, passkey) = await CreateUserWithPasskeyAsync("pks_passwordless");

        using var client = ProjectWebApplicationFactory.CreateProjectClient(factory);
        var (ceremony, options) = await BeginSignInAsync(client);
        Assert.Equal(0, options.GetProperty("allowCredentials").GetArrayLength());
        using var signedIn = await CompleteSignInAsync(client, ceremony, passkey.Sign(options));

        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        AddCookies(client, signedIn);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.Equal(username, me.GetProperty("username").GetString());
    }

    [Fact]
    public async Task A_sign_in_ceremony_counts_once()
    {
        var (_, passkey) = await CreateUserWithPasskeyAsync("pks_once");
        using var client = ProjectWebApplicationFactory.CreateProjectClient(factory);
        var (ceremony, options) = await BeginSignInAsync(client);
        var assertion = passkey.Sign(options);
        using (await CompleteSignInAsync(client, ceremony, assertion))
        {
        }

        using var replay = await CompleteSignInAsync(client, ceremony, assertion);
        Assert.Equal("Auth:PasskeyCeremonyExpired", await ErrorCodeAsync(replay));
    }

    [Fact]
    public async Task An_unknown_credential_is_rejected()
    {
        using var stranger = await ProjectWebApplicationFactory.LoginAsync(factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var (_, unregistered) = SoftwareAuthenticator.Create(await PasskeyRequests.BeginAsync(stranger.Client));

        using var client = ProjectWebApplicationFactory.CreateProjectClient(factory);
        var (ceremony, options) = await BeginSignInAsync(client);
        using var rejected = await CompleteSignInAsync(client, ceremony, unregistered.Sign(options));

        Assert.Equal("Auth:PasskeyVerificationFailed", await ErrorCodeAsync(rejected));
    }

    [Fact]
    public async Task Failed_assertions_for_a_known_account_count_toward_the_lockout()
    {
        var (username, passkey) = await CreateUserWithPasskeyAsync("pks_lockout");
        await ProjectWebApplicationFactory.SetTenantSettingAsync(factory, SettingConstant.Security.LockoutMaxFailedAttempts, "2");
        try
        {
            using var client = ProjectWebApplicationFactory.CreateProjectClient(factory);
            // 先成功用一次：存下的计数器大于 0，之后的 0 才算回退
            var (first, options) = await BeginSignInAsync(client);
            using (var ok = await CompleteSignInAsync(client, first, passkey.Sign(options)))
            {
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            }

            (first, options) = await BeginSignInAsync(client);
            using (var wrongOrigin = await CompleteSignInAsync(client, first, passkey.Sign(options, origin: "https://evil.example")))
            {
                Assert.Equal("Auth:PasskeyVerificationFailed", await ErrorCodeAsync(wrongOrigin));
            }

            // 计数器回退：签名本身对，计数器比登记时还小
            (first, options) = await BeginSignInAsync(client);
            using (var cloned = await CompleteSignInAsync(client, first, passkey.Sign(options, signCount: 0)))
            {
                Assert.Equal("Auth:UserTemporarilyLockedOut", await ErrorCodeAsync(cloned));
            }

            using var password = await client.PostAsJsonAsync("/api/v1/auth/session-login", new { UsernameOrEmail = username, Password });
            Assert.Equal("Auth:UserTemporarilyLockedOut", await ErrorCodeAsync(password));
        }
        finally
        {
            await ProjectWebApplicationFactory.SetTenantSettingAsync(factory, SettingConstant.Security.LockoutMaxFailedAttempts, null);
        }
    }

    [Fact]
    public async Task After_a_password_the_passkey_is_the_default_second_step()
    {
        var (username, passkey) = await CreateUserWithPasskeyAsync("pks_second");

        using var client = ProjectWebApplicationFactory.CreateProjectClient(factory);
        using var first = await client.PostAsJsonAsync("/api/v1/auth/session-login", new { UsernameOrEmail = username, Password });
        var prompt = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(prompt.GetProperty("requiresTwoFactor").GetBoolean());
        Assert.Equal("passkey", prompt.GetProperty("defaultTwoFactorMethod").GetString());
        Assert.Equal(["passkey", "recoveryCode"], prompt.GetProperty("twoFactorMethods").EnumerateArray().Select(m => m.GetString()));
        var token = prompt.GetProperty("twoFactorToken").GetString()!;

        // 没取过选项就交来的断言不认，也不计数
        using (var early = await client.PostAsJsonAsync("/api/v1/auth/two-factor", new { token, passkey = new JsonObject { ["id"] = "x" } }))
        {
            Assert.Equal("Auth:PasskeyCeremonyExpired", await ErrorCodeAsync(early));
        }

        using var optionsResponse = await client.PostAsJsonAsync("/api/v1/auth/two-factor/passkey-options", new { token });
        var options = (await optionsResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("options");
        Assert.Equal(1, options.GetProperty("allowCredentials").GetArrayLength());

        using var signedIn = await client.PostAsJsonAsync("/api/v1/auth/two-factor", new { token, passkey = passkey.Sign(options) });
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
    }

    [Fact]
    public async Task A_passkey_alone_satisfies_a_two_factor_requirement()
    {
        var (_, passkey) = await CreateUserWithPasskeyAsync("pks_required");
        await ProjectWebApplicationFactory.SetTenantSettingAsync(factory, SettingConstant.Security.RequireTwoFactor, "true");
        try
        {
            using var client = ProjectWebApplicationFactory.CreateProjectClient(factory);
            var (ceremony, options) = await BeginSignInAsync(client);
            using var signedIn = await CompleteSignInAsync(client, ceremony, passkey.Sign(options));
            AddCookies(client, signedIn);

            var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
            Assert.False(me.GetProperty("twoFactorSetupRequired").GetBoolean());
        }
        finally
        {
            await ProjectWebApplicationFactory.SetTenantSettingAsync(factory, SettingConstant.Security.RequireTwoFactor, null);
        }
    }

    private async Task<(string Username, SoftwarePasskey Passkey)> CreateUserWithPasskeyAsync(string prefix)
    {
        var username = $"{prefix}_{Guid.NewGuid():N}"[..30];
        using (var admin = await ProjectWebApplicationFactory.LoginAsync(factory, "admin", ProjectWebApplicationFactory.TestAdminPassword))
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

        using var session = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);
        var (_, passkey) = await PasskeyRequests.RegisterAsync(session.Client);
        return (username, passkey);
    }

    private static async Task<(string Ceremony, JsonElement Options)> BeginSignInAsync(HttpClient client)
    {
        using var begin = await client.PostAsJsonAsync("/api/v1/auth/passkey-login/options", new { });
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        var body = await begin.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("ceremony").GetString()!, body.GetProperty("options"));
    }

    private static Task<HttpResponseMessage> CompleteSignInAsync(HttpClient client, string ceremony, JsonObject credential) =>
        client.PostAsJsonAsync("/api/v1/auth/passkey-login", new { ceremony, credential });

    private static void AddCookies(HttpClient client, HttpResponseMessage response) =>
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ",
            response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';', 2)[0])));

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        Assert.False(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
#endif
