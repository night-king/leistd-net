#if (OpenIddictServer)
using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CompanyName.ProjectName.Application.Auth.Options;
using Microsoft.AspNetCore.WebUtilities;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>依赖方发起的退出（RP-Initiated Logout 1.0 §2）：hint 指向当前会话时直接退出，否则必须由用户在本源确认。</summary>
/// <remarks>
/// 断言落在"会话还在不在"和"回没回到依赖方"上：被跨站页面悄悄登出（logout CSRF）、
/// 旧确认页结束了新会话、确认后停在 Identity 不回依赖方，都是要防的结果。
/// </remarks>
public sealed class OidcLogoutTests(ProjectWebApplicationFactory factory) : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Callback = "https://rp.example.test/signin";
    private const string SignedOut = "https://rp.example.test/signout";
    private const string Password = "OidcLogout!Passw0rd";
    private static readonly string Verifier = new('l', 64);

    [Fact]
    public async Task A_hint_for_the_current_session_signs_out_without_confirmation()
    {
        var (client, session) = await SignInAsync();
        var idToken = await IdentityTokenAsync(client, session.Cookie);

        // 依赖方的自动提交表单是跨站 POST，Lax 会话 Cookie 不随行；缓存后的顶层 GET 重入才带上它
        using var anonymous = Browser();
        using var cached = await anonymous.PostAsync("/connect/logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id_token_hint"] = idToken, ["post_logout_redirect_uri"] = SignedOut, ["state"] = "rp-state"
        }));
        var reentry = OidcRequestCaching.CachedLocation(anonymous, cached, "/connect/logout");
        Assert.DoesNotContain("id_token_hint", reentry);
        using var browser = Browser(session.Cookie);
        using var response = await browser.GetAsync(reentry);

        AssertReturnedToClient(response);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Without_a_hint_the_user_confirms_on_the_same_origin_and_returns_to_the_client()
    {
        var (client, session) = await SignInAsync();
        using var browser = Browser(session.Cookie);

        var (requestUri, confirmation) = await ConfirmationPageAsync(browser, client);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        var page = await ConfirmationInfoAsync(browser, requestUri, confirmation);
        Assert.True(page.Info.GetProperty("isValid").GetBoolean());
        Assert.Equal(client, page.Info.GetProperty("applicationName").GetString());

        using var confirmed = await ConfirmAsync(session.Cookie, page, requestUri, confirmation);

        AssertReturnedToClient(confirmed);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_hint_for_another_session_requires_confirmation()
    {
        var (client, first) = await SignInAsync();
        var idToken = await IdentityTokenAsync(client, first.Cookie);
        using var second = await factory.LoginAsync(first.Username, Password);
        using var browser = Browser(second.Cookie);

        using var response = await browser.GetCachedAsync(QueryHelpers.AddQueryString("/connect/logout", new Dictionary<string, string?>
        {
            ["id_token_hint"] = idToken, ["post_logout_redirect_uri"] = SignedOut, ["state"] = "rp-state"
        }));

        Assert.StartsWith("/auth/logout-confirm?", response.Headers.Location!.OriginalString);
        Assert.DoesNotContain(idToken, response.Headers.Location.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await second.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData("missing-antiforgery")]
    [InlineData("forged-confirmation")]
    [InlineData("other-request")]
    public async Task Confirmation_without_its_protections_asks_again_and_keeps_the_session(string failure)
    {
        var (client, session) = await SignInAsync();
        using var browser = Browser(session.Cookie);
        var (requestUri, confirmation) = await ConfirmationPageAsync(browser, client);
        var page = await ConfirmationInfoAsync(browser, requestUri, confirmation);
        var (otherRequestUri, _) = await ConfirmationPageAsync(browser, client);

        using var response = failure switch
        {
            "missing-antiforgery" => await ConfirmAsync(session.Cookie, page with { Token = "" }, requestUri, confirmation),
            "forged-confirmation" => await ConfirmAsync(session.Cookie, page, requestUri, confirmation[..^4] + "AAAA"),
            _ => await ConfirmAsync(session.Cookie, page, otherRequestUri, confirmation)
        };

        Assert.StartsWith("/auth/logout-confirm?", response.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_stale_confirmation_cannot_end_a_newer_session()
    {
        var (client, first) = await SignInAsync();
        using var firstBrowser = Browser(first.Cookie);
        var (requestUri, confirmation) = await ConfirmationPageAsync(firstBrowser, client);
        // 同一用户在同一浏览器重新登录：会话标识变了，旧页上的确认不能结束新会话
        using var second = await factory.LoginAsync(first.Username, Password);
        using var secondBrowser = Browser(second.Cookie);
        var page = await ConfirmationInfoAsync(secondBrowser, requestUri, confirmation);
        Assert.False(page.Info.GetProperty("isValid").GetBoolean());
        var (otherRequestUri, otherConfirmation) = await ConfirmationPageAsync(secondBrowser, client);
        var current = await ConfirmationInfoAsync(secondBrowser, otherRequestUri, otherConfirmation);

        using var response = await ConfirmAsync(second.Cookie, current, requestUri, confirmation);

        Assert.StartsWith("/auth/logout-confirm?", response.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await second.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_confirmed_logout_request_cannot_be_replayed()
    {
        var (client, session) = await SignInAsync();
        using var browser = Browser(session.Cookie);
        var (requestUri, confirmation) = await ConfirmationPageAsync(browser, client);
        var page = await ConfirmationInfoAsync(browser, requestUri, confirmation);
        using (var confirmed = await ConfirmAsync(session.Cookie, page, requestUri, confirmation))
            AssertReturnedToClient(confirmed);

        using var replayed = await ConfirmAsync(session.Cookie, page, requestUri, confirmation);

        // 退出完成时 request token 已兑现：OpenIddict 直接拒绝这个 request_uri，不再回到依赖方
        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);
        Assert.Null(replayed.Headers.Location);
    }

    [Fact]
    public async Task Without_a_session_the_client_gets_its_logout_callback_directly()
    {
        var (client, _) = await SignInAsync();
        using var anonymous = Browser();

        using var response = await anonymous.GetCachedAsync(QueryHelpers.AddQueryString("/connect/logout", new Dictionary<string, string?>
        {
            ["client_id"] = client, ["post_logout_redirect_uri"] = SignedOut, ["state"] = "rp-state"
        }));

        AssertReturnedToClient(response);
    }

    private static void AssertReturnedToClient(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith(SignedOut, response.Headers.Location!.AbsoluteUri);
        Assert.Equal("rp-state", QueryHelpers.ParseQuery(response.Headers.Location.Query)["state"].ToString());
    }

    private async Task<(string RequestUri, string Confirmation)> ConfirmationPageAsync(HttpClient browser, string client)
    {
        using var response = await browser.GetCachedAsync(QueryHelpers.AddQueryString("/connect/logout", new Dictionary<string, string?>
        {
            ["client_id"] = client, ["post_logout_redirect_uri"] = SignedOut, ["state"] = "rp-state"
        }));
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/auth/logout-confirm?", location);
        var query = QueryHelpers.ParseQuery(new Uri(browser.BaseAddress!, location).Query);
        Assert.Equal(["confirmation", "request_uri"], query.Keys.Order());
        return (query["request_uri"].ToString(), query["confirmation"].ToString());
    }

    private sealed record ConfirmationPage(JsonElement Info, string FieldName, string Token, string AntiforgeryCookie);

    private static async Task<ConfirmationPage> ConfirmationInfoAsync(HttpClient browser, string requestUri, string confirmation)
    {
        using var response = await browser.GetAsync(QueryHelpers.AddQueryString("/api/v1/auth/logout-confirmation",
            new Dictionary<string, string?> { ["request_uri"] = requestUri, ["confirmation"] = confirmation }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var info = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (!info.GetProperty("isValid").GetBoolean()) return new ConfirmationPage(info, "", "", "");
        var antiforgeryCookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.Contains("Antiforgery", StringComparison.Ordinal)).Split(';')[0];
        return new ConfirmationPage(info, info.GetProperty("antiforgeryFieldName").GetString()!,
            info.GetProperty("antiforgeryToken").GetString()!, antiforgeryCookie);
    }

    // 确认页的表单 POST：本源、带会话与防伪 Cookie、防伪字段、request_uri 与确认凭据
    private async Task<HttpResponseMessage> ConfirmAsync(string sessionCookie, ConfirmationPage page, string requestUri, string confirmation)
    {
        using var browser = Browser(string.Join("; ", new[] { sessionCookie, page.AntiforgeryCookie }.Where(value => value.Length > 0)));
        browser.DefaultRequestHeaders.Add("Origin", "https://localhost");
        var form = new Dictionary<string, string> { ["request_uri"] = requestUri, ["confirmation"] = confirmation };
        if (page.FieldName.Length > 0) form[page.FieldName] = page.Token;
        return await browser.PostAsync("/connect/logout", new FormUrlEncodedContent(form));
    }

    private HttpClient Browser(string? cookie = null)
    {
        var client = factory.CreateProjectClient();
        client.BaseAddress = new Uri("https://localhost");
        if (cookie is not null) client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private sealed record SignedInUser(string Username, AuthenticatedSession Session) : IDisposable
    {
        public HttpClient Client => Session.Client;
        public string Cookie => Session.Cookie;
        public void Dispose() => Session.Dispose();
    }

    // 登记一个会话绑定的浏览器客户端，并以新用户登录 Identity
    private async Task<(string Client, SignedInUser Session)> SignInAsync()
    {
        var username = $"logout_{Guid.NewGuid():N}"[..30];
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        using (var create = await admin.Client.PostAsJsonAsync("/api/v1/users", new
               { Username = username, Email = $"{username}@example.test", Password, IsActive = true }))
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var client = $"logout-{Guid.NewGuid():N}";
        using (var registered = await admin.Client.PostAsJsonAsync("/api/v1/open-applications", new
               {
                   clientId = client, applicationType = "web", clientType = "public", sessionBound = true,
                   redirectUris = new[] { Callback }, postLogoutRedirectUris = new[] { SignedOut },
                   permissions = new[] { "ept:authorization", "ept:token", "ept:end_session", "gt:authorization_code", "rst:code", "scp:openid", $"scp:{new OAuthResourceOptions().Resource}" },
                   requirements = new[] { "ft:pkce" }
               }))
            Assert.True(registered.IsSuccessStatusCode, await registered.Content.ReadAsStringAsync());
        return (client, new SignedInUser(username, await factory.LoginAsync(username, Password)));
    }

    private async Task<string> IdentityTokenAsync(string client, string cookie)
    {
        using var browser = Browser(cookie);
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));
        using var authorize = await browser.GetCachedAsync(
            $"/connect/authorize?client_id={client}&redirect_uri={Uri.EscapeDataString(Callback)}&response_type=code" +
            $"&scope=openid%20{new OAuthResourceOptions().Resource}&code_challenge={challenge}&code_challenge_method=S256");
        Assert.StartsWith(Callback, authorize.Headers.Location!.AbsoluteUri);
        using var exchange = Browser();
        using var token = await exchange.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.AuthorizationCode, ["client_id"] = client, ["redirect_uri"] = Callback,
            ["code"] = QueryHelpers.ParseQuery(authorize.Headers.Location.Query)["code"].ToString(), ["code_verifier"] = Verifier
        }));
        Assert.True(token.IsSuccessStatusCode, await token.Content.ReadAsStringAsync());
        return (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id_token").GetString()!;
    }
}
#endif
