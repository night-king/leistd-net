#if (OpenIddictServer)
using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CompanyName.ProjectName.Application.OpenApplications.Dtos;
using CompanyName.ProjectName.Domain.Auth.Entities;
using CompanyName.ProjectName.Domain.Auth.Options;
using CompanyName.ProjectName.Application.Auth.Options;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static OpenIddict.Abstractions.OpenIddictConstants;
using CompanyName.ProjectName.Application.OpenApplications;
using OpenIddict.Abstractions;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>会话绑定的开放应用：授权码与刷新令牌跟随签发时的 Identity 会话，会话结束后不能再续期。</summary>
/// <remarks>
/// 断言都落在"那份刷新令牌还能不能换出令牌"上：BFF 的服务端会话靠它续命，
/// 用户在 Identity 退出或被撤销设备后它仍能换出令牌，就是要防的事。
/// </remarks>
public sealed class OpenApplicationSessionBindingTests(ProjectWebApplicationFactory factory) : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Callback = "https://localhost/session-bound";
    private const string Password = "SessionBound!Passw0rd";
    private static readonly string Verifier = new('v', 64);

    public enum SessionEnd { SignOut, Revoke, Idle }

    [Theory]
    [InlineData(SessionEnd.SignOut)]
    [InlineData(SessionEnd.Revoke)]
    [InlineData(SessionEnd.Idle)]
    public async Task Bound_client_grants_follow_the_identity_session_and_stop_renewing_when_it_ends(SessionEnd end)
    {
        var username = await CreateUserAsync();
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var client = await CreateClientAsync(admin.Client, sessionBound: true);
        using var mine = await factory.LoginAsync(username, Password);
        using var other = await factory.LoginAsync(username, Password);
        var sessionId = await CurrentSessionIdAsync(other.Client);

        var tokens = await AuthorizeAsync(client, other.Cookie);
        Assert.Equal(sessionId.ToString(), Claim(tokens.IdentityToken, "sid"));
        Assert.Null(Claim(tokens.AccessToken, "sid"));

        // 会话仍在：续期成功，轮换出的刷新令牌与新 id_token 都保持同一会话
        var renewed = await RefreshAsync(client, tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, renewed.Status);
        Assert.Equal(sessionId.ToString(), Claim(renewed.IdentityToken!, "sid"));
        Assert.Null(Claim(renewed.AccessToken!, "sid"));

        switch (end)
        {
            case SessionEnd.SignOut:
                Assert.Equal(HttpStatusCode.OK, (await other.Client.PostAsync("/api/v1/auth/logout", null)).StatusCode);
                break;
            case SessionEnd.Revoke:
                Assert.Equal(HttpStatusCode.OK, (await mine.Client.DeleteAsync($"/api/v1/auth/me/sessions/{sessionId}")).StatusCode);
                break;
            case SessionEnd.Idle:
                await AgeSessionPastIdleTimeoutAsync(sessionId);
                break;
        }

        var rejected = await RefreshAsync(client, renewed.RefreshToken!);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.Status);
        Assert.Equal(Errors.InvalidGrant, rejected.Error);
        // 别的会话不受牵连
        Assert.Equal(HttpStatusCode.OK, (await mine.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Renewal_checks_do_not_extend_the_identity_session()
    {
        var username = await CreateUserAsync();
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var client = await CreateClientAsync(admin.Client, sessionBound: true);
        using var session = await factory.LoginAsync(username, Password);
        var sessionId = await CurrentSessionIdAsync(session.Client);
        var tokens = await AuthorizeAsync(client, session.Cookie);
        var aged = await AgeSessionAsync(sessionId, TimeSpan.FromHours(1));

        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, tokens.RefreshToken)).Status);
        Assert.Equal(aged, await LastSeenTimeAsync(sessionId));
    }

    [Fact]
    public async Task Unbound_client_keeps_renewing_after_the_session_ends()
    {
        var username = await CreateUserAsync();
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var client = await CreateClientAsync(admin.Client, sessionBound: false);
        using var session = await factory.LoginAsync(username, Password);
        var tokens = await AuthorizeAsync(client, session.Cookie);
        Assert.Null(Claim(tokens.IdentityToken, "sid"));

        Assert.Equal(HttpStatusCode.OK, (await session.Client.PostAsync("/api/v1/auth/logout", null)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, tokens.RefreshToken)).Status);
    }

    [Fact]
    public async Task Grants_issued_before_binding_was_enabled_must_be_reauthorized()
    {
        var username = await CreateUserAsync();
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var client = await CreateClientAsync(admin.Client, sessionBound: false);
        using var session = await factory.LoginAsync(username, Password);
        var tokens = await AuthorizeAsync(client, session.Cookie);

        await UpdateClientAsync(admin.Client, client, sessionBound: true);

        var rejected = await RefreshAsync(client, tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.Status);
        Assert.Equal(Errors.InvalidGrant, rejected.Error);
        // 重新走授权码流程即拿到绑定会话的授权
        var reauthorized = await AuthorizeAsync(client, session.Cookie);
        Assert.Equal((await CurrentSessionIdAsync(session.Client)).ToString(), Claim(reauthorized.IdentityToken, "sid"));
    }

    [Fact]
    public async Task Clearing_the_binding_does_not_release_grants_issued_under_it()
    {
        var username = await CreateUserAsync();
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var client = await CreateClientAsync(admin.Client, sessionBound: true);
        using var session = await factory.LoginAsync(username, Password);
        var tokens = await AuthorizeAsync(client, session.Cookie);

        await UpdateClientAsync(admin.Client, client, sessionBound: false);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.PostAsync("/api/v1/auth/logout", null)).StatusCode);

        var rejected = await RefreshAsync(client, tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.Status);
        Assert.Equal(Errors.InvalidGrant, rejected.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_binding_must_be_chosen_explicitly(bool explicitNull)
    {
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var body = ClientBody($"bound-{Guid.NewGuid():N}");
        if (explicitNull) body["sessionBound"] = null;

        using var create = await admin.Client.PostAsJsonAsync("/api/v1/open-applications", body);
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);

        var clientId = await CreateClientAsync(admin.Client, sessionBound: true);
        var update = ClientBody(clientId);
        update.Remove("clientId");
        if (explicitNull) update["sessionBound"] = null;
        using var put = await admin.Client.PutAsJsonAsync($"/api/v1/open-applications/{applicationIds[clientId]}", update);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.True(await SessionBoundAsync(admin.Client, clientId));
    }

    // 存量登记（早于该设置）与被改坏的登记：缺键按未绑定处理，无法识别的值按绑定处理（宁可续期失败也不放宽）
    [Theory]
    [InlineData(null, false)]
    [InlineData("yes", true)]
    public async Task Registrations_without_a_valid_setting_follow_the_documented_defaults(string? stored, bool bound)
    {
        var username = await CreateUserAsync();
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var client = await CreateClientAsync(admin.Client, sessionBound: true);
        await RewriteSettingAsync(client, stored);
        Assert.Equal(stored is null ? null : true, await SessionBoundAsync(admin.Client, client));
        using var session = await factory.LoginAsync(username, Password);

        var tokens = await AuthorizeAsync(client, session.Cookie);

        Assert.Equal(bound, Claim(tokens.IdentityToken, "sid") is not null);
    }

    // 管理 API 只改自己这一项设置：登记里其他设置原样保留
    [Fact]
    public async Task Updating_the_binding_keeps_unrelated_settings()
    {
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var client = await CreateClientAsync(admin.Client, sessionBound: true);
        await WithApplicationAsync(client, async (manager, application) =>
        {
            var descriptor = new OpenIddictApplicationDescriptor();
            await manager.PopulateAsync(descriptor, application);
            descriptor.Settings["acme:custom"] = "kept";
            await manager.UpdateAsync(application, descriptor);
        });

        await UpdateClientAsync(admin.Client, client, sessionBound: false);

        await WithApplicationAsync(client, async (manager, application) =>
        {
            var settings = await manager.GetSettingsAsync(application);
            Assert.Equal("kept", settings["acme:custom"]);
            Assert.Equal("false", settings[OpenApplicationSettings.SessionBound]);
        });
    }

    // 绕过管理 API 直接改写登记，模拟升级前的存量数据或被手工改坏的值
    private Task RewriteSettingAsync(string clientId, string? value) => WithApplicationAsync(clientId, async (manager, application) =>
    {
        var descriptor = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(descriptor, application);
        if (value is null) descriptor.Settings.Remove(OpenApplicationSettings.SessionBound);
        else descriptor.Settings[OpenApplicationSettings.SessionBound] = value;
        await manager.UpdateAsync(application, descriptor);
    });

    private async Task WithApplicationAsync(string clientId,
        Func<IOpenIddictApplicationManager, object, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        await action(manager, (await manager.FindByClientIdAsync(clientId))!);
    }

    private static Dictionary<string, object?> ClientBody(string clientId) => new()
    {
        ["clientId"] = clientId, ["applicationType"] = "web", ["clientType"] = "public",
        ["redirectUris"] = new[] { Callback }, ["postLogoutRedirectUris"] = Array.Empty<string>(),
        ["permissions"] = new[]
        {
            "ept:authorization", "ept:token", "gt:authorization_code", "gt:refresh_token", "rst:code",
            "scp:openid", "scp:offline_access", $"scp:{new OAuthResourceOptions().Resource}"
        },
        ["requirements"] = new[] { "ft:pkce" }
    };

    private async Task<string> CreateClientAsync(HttpClient admin, bool sessionBound)
    {
        var clientId = $"bound-{Guid.NewGuid():N}";
        var body = ClientBody(clientId);
        body["sessionBound"] = sessionBound;
        using var response = await admin.PostAsJsonAsync("/api/v1/open-applications", body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var output = (await response.Content.ReadFromJsonAsync<OpenApplicationOutputDto>())!;
        Assert.Equal(sessionBound, output.SessionBound);
        applicationIds[clientId] = output.Id;
        return clientId;
    }

    private async Task UpdateClientAsync(HttpClient admin, string clientId, bool sessionBound)
    {
        var body = ClientBody(clientId);
        body.Remove("clientId");
        body["sessionBound"] = sessionBound;
        using var response = await admin.PutAsJsonAsync($"/api/v1/open-applications/{applicationIds[clientId]}", body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(sessionBound, await SessionBoundAsync(admin, clientId));
    }

    // 测试里登记的 client_id 与应用 Id 一一对应，按 client_id 记下创建结果的 Id
    private readonly Dictionary<string, string> applicationIds = [];

    private async Task<bool?> SessionBoundAsync(HttpClient admin, string clientId) =>
        (await admin.GetFromJsonAsync<OpenApplicationOutputDto>($"/api/v1/open-applications/{applicationIds[clientId]}"))!.SessionBound;

    private HttpClient Browser(string? cookie = null)
    {
        var client = factory.CreateProjectClient();
        client.BaseAddress = new Uri("https://localhost");
        if (cookie is not null) client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private async Task<(string AccessToken, string IdentityToken, string RefreshToken)> AuthorizeAsync(string clientId, string cookie)
    {
        using var browser = Browser(cookie);
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));
        using var authorize = await browser.GetCachedAsync(
            $"/connect/authorize?client_id={clientId}&redirect_uri={Uri.EscapeDataString(Callback)}&response_type=code" +
            $"&scope=openid%20offline_access%20{new OAuthResourceOptions().Resource}&code_challenge={challenge}&code_challenge_method=S256");
        Assert.Equal(HttpStatusCode.Found, authorize.StatusCode);
        Assert.StartsWith(Callback, authorize.Headers.Location!.AbsoluteUri);
        var code = authorize.Headers.Location.Query.TrimStart('?').Split('&')
            .Single(part => part.StartsWith("code=", StringComparison.Ordinal))[5..];

        var result = await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.AuthorizationCode, ["client_id"] = clientId,
            ["code"] = Uri.UnescapeDataString(code), ["redirect_uri"] = Callback, ["code_verifier"] = Verifier
        });
        Assert.Equal(HttpStatusCode.OK, result.Status);
        return (result.AccessToken!, result.IdentityToken!, result.RefreshToken!);
    }

    private Task<TokenResult> RefreshAsync(string clientId, string refreshToken) => TokenAsync(new Dictionary<string, string>
    {
        ["grant_type"] = GrantTypes.RefreshToken, ["client_id"] = clientId, ["refresh_token"] = refreshToken
    });

    private async Task<TokenResult> TokenAsync(Dictionary<string, string> form)
    {
        using var client = Browser();
        using var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string? Read(string name) => body.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
        return new TokenResult(response.StatusCode, Read("access_token"), Read("id_token"), Read("refresh_token"), Read("error"));
    }

    private static string? Claim(string jwt, string name)
    {
        using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(jwt.Split('.')[1]));
        return payload.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    private async Task<string> CreateUserAsync()
    {
        var username = $"bound_{Guid.NewGuid():N}"[..30];
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var create = await admin.Client.PostAsJsonAsync("/api/v1/users", new
        {
            Username = username, Email = $"{username}@example.test", Password, IsActive = true
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        return username;
    }

    private static async Task<Guid> CurrentSessionIdAsync(HttpClient client)
    {
        using var body = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me/sessions"));
        return body.RootElement.EnumerateArray().Single(session => session.GetProperty("isCurrent").GetBoolean())
            .GetProperty("id").GetGuid();
    }

    private async Task AgeSessionPastIdleTimeoutAsync(Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var idleTimeout = scope.ServiceProvider.GetRequiredService<IOptions<UserSessionOptions>>().Value.IdleTimeout;
        await AgeSessionAsync(sessionId, idleTimeout + TimeSpan.FromMinutes(1));
    }

    // 最近活动推到过去：登录流程不会造出这样的行，直接写库
    private async Task<DateTime> AgeSessionAsync(Guid sessionId, TimeSpan age)
    {
        var lastSeen = DateTime.SpecifyKind(DateTime.UtcNow.Subtract(age), DateTimeKind.Utc);
        lastSeen = lastSeen.AddTicks(-(lastSeen.Ticks % TimeSpan.TicksPerMillisecond));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        Assert.Equal(1, await db.Set<UserSession>().IgnoreQueryFilters().Where(session => session.Id == sessionId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.LastSeenTime, lastSeen)));
        return lastSeen;
    }

    private async Task<DateTime> LastSeenTimeAsync(Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        return (await db.Set<UserSession>().IgnoreQueryFilters().SingleAsync(session => session.Id == sessionId)).LastSeenTime;
    }

    private sealed record TokenResult(HttpStatusCode Status, string? AccessToken, string? IdentityToken, string? RefreshToken, string? Error);
}
#endif
