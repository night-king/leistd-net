#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Application.Settings.Provider;
using CompanyName.ProjectName.Infrastructure.Auth.Passkeys;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>
/// 本人登记与管理通行密钥：响应经真实的 WebAuthn 校验；第一把带来账号级恢复码；增删换安全版本并让其他会话退出；
/// 组织要求两步验证时不能删掉最后一个独立因子，受限会话登记一把即解除限制。
/// </summary>
public sealed class PasskeyRegistrationTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Password = "PasskeyRegistration!Pw1";

    [Fact]
    public async Task The_first_passkey_is_listed_issues_recovery_codes_and_signs_out_other_sessions()
    {
        var username = await CreateUserAsync("pk_first");
        using var other = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);
        using var session = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);

        var (result, _) = await PasskeyRequests.RegisterAsync(session.Client, "My laptop");

        Assert.Equal(10, result.GetProperty("recoveryCodes").GetArrayLength());
        Assert.Equal("My laptop", result.GetProperty("passkey").GetProperty("name").GetString());
        var status = await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/two-factor");
        Assert.True(status.GetProperty("passkeysAvailable").GetBoolean());
        Assert.True((await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/security-config"))
            .GetProperty("passkeysAvailable").GetBoolean());
        Assert.Equal(1, status.GetProperty("passkeys").GetArrayLength());
        Assert.Equal(10, status.GetProperty("recoveryCodesLeft").GetInt32());
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.Client.GetAsync("/api/v1/auth/me")).StatusCode);

        using var admin = await ProjectWebApplicationFactory.LoginAsync(factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var userId = (await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetString()!;
        Assert.Equal(1, await OperationRecordQueries.CountSucceededAsync(
            factory, admin.Client, OperationRecordActions.AuthTwoFactorPasskeyAdded, userId));
    }

    [Fact]
    public async Task A_second_passkey_keeps_the_recovery_codes()
    {
        var username = await CreateUserAsync("pk_second");
        using var session = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);
        await PasskeyRequests.RegisterAsync(session.Client);

        var (result, _) = await PasskeyRequests.RegisterAsync(session.Client, "Phone");

        Assert.Equal(0, result.GetProperty("recoveryCodes").GetArrayLength());
        var status = await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/two-factor");
        Assert.Equal(2, status.GetProperty("passkeys").GetArrayLength());
        Assert.Equal(10, status.GetProperty("recoveryCodesLeft").GetInt32());
    }

    [Fact]
    public async Task A_registration_response_counts_once_and_only_from_an_allowed_origin()
    {
        var username = await CreateUserAsync("pk_replay");
        using var session = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);

        // 来源不在允许列表里：协议校验拒绝
        var options = await PasskeyRequests.BeginAsync(session.Client);
        var (foreign, _) = SoftwareAuthenticator.Create(options);
        RewriteOrigin(foreign, "https://evil.example");
        Assert.Equal("Auth:PasskeyVerificationFailed", await RejectionAsync(session.Client, foreign));

        // 同一份仪式只认一次：交过之后再交，仪式已经不在了
        options = await PasskeyRequests.BeginAsync(session.Client);
        var (response, _) = SoftwareAuthenticator.Create(options);
        using (var first = await session.Client.PostAsJsonAsync("/api/v1/auth/me/two-factor/passkeys", new { credential = response, name = "One" }))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        Assert.Equal("Auth:PasskeyCeremonyExpired", await RejectionAsync(session.Client, response));
    }

    [Fact]
    public async Task A_passkey_is_renamed_and_removed_and_the_last_one_takes_the_recovery_codes()
    {
        var username = await CreateUserAsync("pk_manage");
        using var session = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);
        var (result, _) = await PasskeyRequests.RegisterAsync(session.Client);
        var id = result.GetProperty("passkey").GetProperty("id").GetGuid();

        using (var rename = await session.Client.PutAsJsonAsync($"/api/v1/auth/me/two-factor/passkeys/{id}/name", new { name = "Renamed" }))
        {
            Assert.Equal("Renamed", (await rename.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
        }

        using (var remove = await session.Client.PostAsJsonAsync($"/api/v1/auth/me/two-factor/passkeys/{id}/remove", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        }

        var status = await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/two-factor");
        Assert.Equal(0, status.GetProperty("passkeys").GetArrayLength());
        Assert.Equal(0, status.GetProperty("recoveryCodesLeft").GetInt32());

        using var missing = await session.Client.PostAsJsonAsync($"/api/v1/auth/me/two-factor/passkeys/{id}/remove", new { });
        Assert.Equal("Auth:PasskeyNotFound", await ErrorCodeAsync(missing));
    }

    [Fact]
    public async Task Under_a_requirement_a_restricted_session_registers_one_and_cannot_remove_the_last()
    {
        var username = await CreateUserAsync("pk_required");
        await ProjectWebApplicationFactory.SetTenantSettingAsync(factory, SettingConstant.Security.RequireTwoFactor, "true");
        try
        {
            using var session = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);
            Assert.True((await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me"))
                .GetProperty("twoFactorSetupRequired").GetBoolean());

            var (result, _) = await PasskeyRequests.RegisterAsync(session.Client);

            Assert.False((await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me"))
                .GetProperty("twoFactorSetupRequired").GetBoolean());
            var id = result.GetProperty("passkey").GetProperty("id").GetGuid();
            using var remove = await session.Client.PostAsJsonAsync($"/api/v1/auth/me/two-factor/passkeys/{id}/remove", new { });
            Assert.Equal("Auth:TwoFactorRequiredByPolicy", await ErrorCodeAsync(remove));
        }
        finally
        {
            await ProjectWebApplicationFactory.SetTenantSettingAsync(factory, SettingConstant.Security.RequireTwoFactor, null);
        }
    }

    [Fact]
    public async Task An_administrator_reset_removes_the_passkeys_and_the_recovery_codes()
    {
        var username = await CreateUserAsync("pk_reset");
        using var session = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);
        await PasskeyRequests.RegisterAsync(session.Client);
        var userId = (await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetString();
        Assert.True((await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me")).GetProperty("hasPasskeys").GetBoolean());

        using (var admin = await ProjectWebApplicationFactory.LoginAsync(factory, "admin", ProjectWebApplicationFactory.TestAdminPassword))
        using (var reset = await admin.Client.PostAsJsonAsync($"/api/v1/users/{userId}/reset-two-factor", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        }

        using var again = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);
        var status = await again.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/two-factor");
        Assert.Equal(0, status.GetProperty("passkeys").GetArrayLength());
        Assert.Equal(0, status.GetProperty("recoveryCodesLeft").GetInt32());
    }

    [Fact]
    public async Task Without_a_relying_party_passkeys_are_unavailable()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<PasskeyOptions>(options =>
            {
                options.RelyingPartyId = null;
                options.Origins = [];
            })));
        var username = await CreateUserAsync("pk_unconfigured");
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);

        Assert.False((await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/two-factor"))
            .GetProperty("passkeysAvailable").GetBoolean());
        Assert.False((await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/security-config"))
            .GetProperty("passkeysAvailable").GetBoolean());
        using var begin = await session.Client.PostAsJsonAsync("/api/v1/auth/me/two-factor/passkeys/options", new { });
        Assert.Equal("Auth:PasskeyUnavailable", await ErrorCodeAsync(begin));
    }

    private static void RewriteOrigin(JsonObject response, string origin)
    {
        var data = response["response"]!["clientDataJSON"]!.GetValue<string>();
        var json = JsonNode.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(data))!;
        json["origin"] = origin;
        response["response"]!["clientDataJSON"] = System.Buffers.Text.Base64Url.EncodeToString(
            System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
    }

    private static async Task<string?> RejectionAsync(HttpClient session, JsonObject response)
    {
        using var complete = await session.PostAsJsonAsync("/api/v1/auth/me/two-factor/passkeys", new { credential = response, name = "x" });
        return await ErrorCodeAsync(complete);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        Assert.False(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
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
            IsActive = true,
            IsEmailVerified = true
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        return username;
    }
}
#endif
