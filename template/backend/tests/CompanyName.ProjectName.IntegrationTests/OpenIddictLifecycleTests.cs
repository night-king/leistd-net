#if (OpenIddictServer)
using Leistd.Security.AspNetCore.Cookies;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Buffers.Text;
using System.Security.Claims;
using System.Text.Json;
using CompanyName.ProjectName.Application.Shared;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using CompanyName.ProjectName.Application.OpenApplications.Dtos;
using CompanyName.ProjectName.Application.Auth.Options;
using Leistd.BackgroundJobs.Recurring;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Leistd.Security.OpenIddict.Server.Pruning;
using OpenIddict.Core;
using OpenIddict.EntityFrameworkCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CompanyName.ProjectName.IntegrationTests;

public sealed class OpenIddictLifecycleTests(ProjectWebApplicationFactory factory) : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Callback = "https://localhost/oidc-test";

    private async Task<string> CreatePublicClientAsync(HttpClient admin)
    {
        var id = $"prompt-{Guid.NewGuid():N}";
        var response = await admin.PostAsJsonAsync("/api/v1/open-applications", new
        {
            sessionBound = false,
            clientId = id, applicationType = "web", clientType = "public", redirectUris = new[] { Callback },
            permissions = new[] { "ept:authorization", "ept:token", "gt:authorization_code", "rst:code", "scp:openid", $"scp:{new OAuthResourceOptions().Resource}" },
            requirements = new[] { "ft:pkce" }
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var output = await response.Content.ReadFromJsonAsync<OpenApplicationOutputDto>();
        Assert.NotNull(output);
        using var scope = factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var app = await manager.FindByClientIdAsync(id);
        Assert.Equal(ConsentTypes.Implicit, await manager.GetConsentTypeAsync(app!));
        return id;
    }

    private HttpClient Browser(string? cookie = null)
    {
        var client = factory.CreateProjectClient();
        client.BaseAddress = new Uri("https://localhost");
        if (cookie is not null) client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static string AuthorizationUrl(string client, string parameters) =>
        $"/connect/authorize?client_id={client}&redirect_uri={Uri.EscapeDataString(Callback)}&response_type=code" +
        $"&scope=openid%20{new OAuthResourceOptions().Resource}&code_challenge={Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(new string('x', 64))))}&code_challenge_method=S256{parameters}";

    [Fact]
    public async Task Prompt_none_without_a_session_returns_login_required_to_the_protocol_callback()
    {
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var id = await CreatePublicClientAsync(admin.Client);
        using var anonymous = Browser();
        var response = await anonymous.GetCachedAsync(AuthorizationUrl(id, "&prompt=none&state=probe"));
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith(Callback, response.Headers.Location!.AbsoluteUri);
        Assert.Contains("error=login_required", response.Headers.Location.Query);
        Assert.Contains("state=probe", response.Headers.Location.Query);
        Assert.DoesNotContain("/auth/login", response.Headers.Location.AbsoluteUri);
    }

    [Theory]
    [InlineData("&prompt=login")]
    [InlineData("&max_age=0")]
    public async Task Reauthentication_is_proven_for_the_cached_request_and_completes_without_looping(string parameter)
    {
        using var session = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var id = await CreatePublicClientAsync(session.Client);
        // 人为提供旧 auth_time，避免依赖秒级等待；续期不会改变真实认证时间。
        var oldTicket = ReadCookie(session.Cookie);
        var oldIdentity = (ClaimsIdentity)oldTicket.Principal.Identity!;
        foreach (var claim in oldIdentity.FindAll(Claims.AuthenticationTime).ToArray()) oldIdentity.RemoveClaim(claim);
        var oldTime = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        oldIdentity.AddClaim(new Claim(Claims.AuthenticationTime, oldTime.ToString(), ClaimValueTypes.Integer64));
        var oldCookie = CookieOf(oldTicket);
        using var browser = Browser(oldCookie);

        var returnUrl = await ReauthenticationReturnUrlAsync(browser, AuthorizationUrl(id, parameter));
        // 回跳只指向缓存的请求：协议参数全在 request token 里，URL 上只有引用与证明
        Assert.Equal(["client_id", "reauthentication", "request_uri"],
            QueryHelpers.ParseQuery(new Uri(browser.BaseAddress!, returnUrl).Query).Keys.Order());
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        // 证明本身不代替登录：没有新的认证就回跳，仍然要求登录
        var again = await ReauthenticationReturnUrlAsync(browser, returnUrl, cached: false);
        Assert.Equal(RequestUriOf(browser, returnUrl), RequestUriOf(browser, again));
        var otherReturnUrl = await ReauthenticationReturnUrlAsync(browser, AuthorizationUrl(id, parameter));

        using var relogin = Browser(oldCookie);
        using var failed = await relogin.PostAsJsonAsync("/api/v1/auth/session-login", new { usernameOrEmail = "admin", password = "incorrect" });
        Assert.False(failed.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        using var success = await relogin.PostAsJsonAsync("/api/v1/auth/session-login", new
        { usernameOrEmail = "admin", password = ProjectWebApplicationFactory.TestAdminPassword });
        Assert.True(success.IsSuccessStatusCode, await success.Content.ReadAsStringAsync());
        var cookie = success.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(session.Cookie.Split('=')[0] + "=", StringComparison.Ordinal));
        Assert.True(long.Parse(ReadCookie(cookie).Principal.GetClaim(Claims.AuthenticationTime)!) > oldTime);
        using var authenticated = Browser(cookie.Split(';')[0]);

        // 证明绑定在签发它的请求上：挪给另一个缓存请求不算数
        Assert.NotEqual(RequestUriOf(browser, returnUrl), RequestUriOf(browser, otherReturnUrl));
        var transplanted = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = id, ["request_uri"] = RequestUriOf(browser, otherReturnUrl),
            ["reauthentication"] = QueryHelpers.ParseQuery(new Uri(authenticated.BaseAddress!, returnUrl).Query)["reauthentication"]
        });
        using (var moved = await authenticated.GetAsync(transplanted))
            Assert.StartsWith("/auth/login?returnUrl=", moved.Headers.Location!.OriginalString);

        // 新的认证兑现证明：签发授权码，不再循环回登录
        using var completed = await authenticated.GetAsync(returnUrl);
        Assert.Equal(HttpStatusCode.Found, completed.StatusCode);
        Assert.StartsWith(Callback, completed.Headers.Location!.AbsoluteUri);
        Assert.Contains("code=", completed.Headers.Location.Query);
        // 一次性：授权完成时 request token 已兑现，同一回跳地址不能再换出授权码
        using var replayed = await authenticated.GetAsync(returnUrl);
        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);
        Assert.Null(replayed.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, (await authenticated.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    /// <summary>证明要求发生过新的认证（换了会话），不能只看认证时间。</summary>
    /// <remarks>auth_time 只到秒：旧会话与证明同秒、或副本之间时钟有偏差时，单凭"认证时间不早于签发时间"会让旧会话直接兑现。
    /// 这里把旧会话的认证时间拨到证明签发之后，确定性地复现这一情形（用 prompt=login：认证时间在未来时 max_age 本就判定为未过期；
    /// 两者兑现证明走同一段校验）。</remarks>
    [Fact]
    public async Task A_proof_cannot_be_redeemed_by_the_session_that_existed_when_it_was_issued()
    {
        const string parameter = "&prompt=login";
        using var session = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var id = await CreatePublicClientAsync(session.Client);
        var ticket = ReadCookie(session.Cookie);
        var identity = (ClaimsIdentity)ticket.Principal.Identity!;
        foreach (var claim in identity.FindAll(Claims.AuthenticationTime).ToArray()) identity.RemoveClaim(claim);
        identity.AddClaim(new Claim(Claims.AuthenticationTime, DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64));
        using var browser = Browser(CookieOf(ticket));

        var returnUrl = await ReauthenticationReturnUrlAsync(browser, AuthorizationUrl(id, parameter));

        // 没有重新登录、同一个会话带着证明回跳：仍然要求登录
        var again = await ReauthenticationReturnUrlAsync(browser, returnUrl, cached: false);
        Assert.Equal(RequestUriOf(browser, returnUrl), RequestUriOf(browser, again));
    }

    // 签发前就已存在的另一个会话（同一用户在别处的登录）也兑现不了：秒级 auth_time 被拨到签发之后也不能让它冒充新认证
    // （这里只改了 Cookie 里的 auth_time；副本之间真实的时钟偏差不在本用例范围内，见部署文档的时钟要求）
    [Fact]
    public async Task A_proof_cannot_be_redeemed_by_another_session_that_existed_before_it()
    {
        using var first = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var other = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var id = await CreatePublicClientAsync(first.Client);
        using var browser = Browser(first.Cookie);
        var returnUrl = await ReauthenticationReturnUrlAsync(browser, AuthorizationUrl(id, "&prompt=login"));
        var ticket = ReadCookie(other.Cookie);
        var identity = (ClaimsIdentity)ticket.Principal.Identity!;
        foreach (var claim in identity.FindAll(Claims.AuthenticationTime).ToArray()) identity.RemoveClaim(claim);
        identity.AddClaim(new Claim(Claims.AuthenticationTime, DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64));
        using var otherBrowser = Browser(CookieOf(ticket));

        var again = await ReauthenticationReturnUrlAsync(otherBrowser, returnUrl, cached: false);

        Assert.Equal(RequestUriOf(browser, returnUrl), RequestUriOf(browser, again));
    }

    // 正数 max_age 不因证明而放宽：兑现时认证年龄已超过限制，仍要求重新认证
    [Fact]
    public async Task A_proof_does_not_relax_a_positive_max_age_that_has_expired_again()
    {
        using var session = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var id = await CreatePublicClientAsync(session.Client);
        var stale = ReadCookie(session.Cookie);
        var staleIdentity = (ClaimsIdentity)stale.Principal.Identity!;
        foreach (var claim in staleIdentity.FindAll(Claims.AuthenticationTime).ToArray()) staleIdentity.RemoveClaim(claim);
        staleIdentity.AddClaim(new Claim(Claims.AuthenticationTime, DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64));
        using var browser = Browser(CookieOf(stale));
        var returnUrl = await ReauthenticationReturnUrlAsync(browser, AuthorizationUrl(id, "&max_age=60"));

        // 新登录兑现证明；随后把这次认证的时间推到 max_age 之前，模拟用户隔了很久才回跳
        using var relogin = Browser();
        using var success = await relogin.PostAsJsonAsync("/api/v1/auth/session-login", new
        { usernameOrEmail = "admin", password = ProjectWebApplicationFactory.TestAdminPassword });
        var cookie = success.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(session.Cookie.Split('=')[0] + "=", StringComparison.Ordinal));
        var renewed = ReadCookie(cookie);
        var renewedIdentity = (ClaimsIdentity)renewed.Principal.Identity!;
        foreach (var claim in renewedIdentity.FindAll(Claims.AuthenticationTime).ToArray()) renewedIdentity.RemoveClaim(claim);
        renewedIdentity.AddClaim(new Claim(Claims.AuthenticationTime, DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64));
        using var late = Browser(CookieOf(renewed));

        var again = await ReauthenticationReturnUrlAsync(late, returnUrl, cached: false);

        Assert.Equal(RequestUriOf(browser, returnUrl), RequestUriOf(browser, again));
    }

    // 授权端点要求重新登录时给出的回跳地址；cached 表示 url 是首个请求（需先跟随请求缓存的那一跳）
    private static async Task<string> ReauthenticationReturnUrlAsync(HttpClient browser, string url, bool cached = true)
    {
        using var response = cached ? await browser.GetCachedAsync(url) : await browser.GetAsync(url);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/auth/login?returnUrl=", location);
        Assert.EndsWith("&reauthenticate=true", location);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var returnUrl = QueryHelpers.ParseQuery(new Uri(browser.BaseAddress!, location).Query)["returnUrl"].ToString();
        Assert.DoesNotContain("prompt=", returnUrl);
        Assert.DoesNotContain("max_age=", returnUrl);
        return returnUrl;
    }

    private static string RequestUriOf(HttpClient browser, string url) =>
        QueryHelpers.ParseQuery(new Uri(browser.BaseAddress!, url).Query)["request_uri"].ToString();

    private AuthenticationTicket ReadCookie(string cookie)
    {
        var cookieOptions = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AuthenticationSchemeNames.SessionCookie);
        var value = cookie.Split(';')[0].Split('=', 2)[1];
        var reference = cookieOptions.TicketDataFormat.Unprotect(value)!;
        return cookieOptions.SessionStore!.RetrieveAsync(reference.Principal.FindFirst("Microsoft.AspNetCore.Authentication.Cookies-SessionId")!.Value).GetAwaiter().GetResult()!;
    }

    private string CookieOf(AuthenticationTicket ticket)
    {
        var cookieOptions = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AuthenticationSchemeNames.SessionCookie);
        var key = ticket.Properties.Items[DistributedTicketStore.TicketKeyProperty]!;
        cookieOptions.SessionStore!.RenewAsync(key, ticket).GetAwaiter().GetResult();
        var reference = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("Microsoft.AspNetCore.Authentication.Cookies-SessionId", key)], AuthenticationSchemeNames.SessionCookie)),
            ticket.Properties, AuthenticationSchemeNames.SessionCookie);
        return cookieOptions.Cookie.Name + "=" + cookieOptions.TicketDataFormat.Protect(reference);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Max_age_uses_original_authentication_time_even_after_cookie_renewal(bool missingTime)
    {
        using var session = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var id = await CreatePublicClientAsync(session.Client);
        var ticket = ReadCookie(session.Cookie);
        var identity = (ClaimsIdentity)ticket.Principal.Identity!;
        foreach (var claim in identity.FindAll(Claims.AuthenticationTime).ToArray()) identity.RemoveClaim(claim);
        if (!missingTime)
            identity.AddClaim(new Claim(Claims.AuthenticationTime, DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64));
        ticket.Properties.IssuedUtc = DateTimeOffset.UtcNow;
        using var browser = Browser(CookieOf(ticket));
        var response = await browser.GetCachedAsync(AuthorizationUrl(id, "&prompt=none&max_age=30"));
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("error=login_required", response.Headers.Location!.Query);
        Assert.StartsWith(Callback, response.Headers.Location.AbsoluteUri);
    }

    [Fact]
    public async Task Identity_token_auth_time_comes_from_cookie_and_is_a_numeric_date()
    {
        using var session = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var id = await CreatePublicClientAsync(session.Client);
        var ticket = ReadCookie(session.Cookie);
        var original = long.Parse(ticket.Principal.GetClaim(Claims.AuthenticationTime)!);
        ticket.Properties.IssuedUtc = DateTimeOffset.UtcNow.AddSeconds(10);
        using var browser = Browser(CookieOf(ticket));
        var response = await browser.GetCachedAsync(AuthorizationUrl(id, "&max_age=3600"));
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var code = response.Headers.Location!.Query.TrimStart('?').Split('&')
            .Single(part => part.StartsWith("code=", StringComparison.Ordinal))[5..];
        using var exchange = Browser();
        var token = await exchange.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = id,
            ["code"] = Uri.UnescapeDataString(code), ["redirect_uri"] = Callback, ["code_verifier"] = new string('x', 64)
        }));
        Assert.True(token.IsSuccessStatusCode, await token.Content.ReadAsStringAsync());
        using var responseJson = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
        var jwt = responseJson.RootElement.GetProperty("id_token").GetString()!.Split('.')[1];
        using var claims = JsonDocument.Parse(Base64Url.DecodeFromChars(jwt));
        Assert.Equal(original, claims.RootElement.GetProperty("auth_time").GetInt64());
    }

    [Fact]
    public async Task Pruning_uses_registered_cluster_job_and_keeps_young_or_valid_records()
    {
        // 走官方的批量清理（ExecuteDelete），与生产一致。批量删除不经过变更跟踪器，
        // 同一作用域里仍能读到已删的实体，因此清理后换一个作用域读库里的真实状态
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<OpenIddictCoreOptions>(options => options.DisableEntityCaching = true)));
        using var scope = host.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var now = DateTimeOffset.UtcNow;
        async Task<object> TokenAsync(DateTimeOffset created, DateTimeOffset expiry) => await tokens.CreateAsync(new OpenIddictTokenDescriptor
        {
            Subject = Guid.NewGuid().ToString(), Type = TokenTypeHints.AccessToken, Status = Statuses.Valid,
            CreationDate = created, ExpirationDate = expiry
        });
        var oldExpired = await TokenAsync(now.AddDays(-15), now.AddDays(-1));
        var youngExpired = await TokenAsync(now.AddDays(-13), now.AddDays(-1));
        var oldValid = await TokenAsync(now.AddDays(-15), now.AddDays(1));
        async Task<object> AuthorizationAsync(DateTimeOffset created, string status, string type) => await authorizations.CreateAsync(new OpenIddictAuthorizationDescriptor
        {
            Subject = Guid.NewGuid().ToString(), Type = type, Status = status, CreationDate = created
        });
        var oldRevoked = await AuthorizationAsync(now.AddDays(-15), Statuses.Revoked, AuthorizationTypes.Permanent);
        var youngRevoked = await AuthorizationAsync(now.AddDays(-13), Statuses.Revoked, AuthorizationTypes.Permanent);
        var oldPermanent = await AuthorizationAsync(now.AddDays(-15), Statuses.Valid, AuthorizationTypes.Permanent);
        var oldAdHoc = await AuthorizationAsync(now.AddDays(-15), Statuses.Valid, AuthorizationTypes.AdHoc);
        var definition = scope.ServiceProvider.GetServices<RecurringJobDefinition>().Single(job => job.Name == OpenIddictPruningJob.Name);
        Assert.Equal(RecurringJobScope.Cluster, definition.Scope);
        // 真实 PostgreSQL 上走官方批量删除：存储没有关掉批量操作，也没有退回内存或其他提供程序
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", scope.ServiceProvider.GetRequiredService<OpenIddictDbContext>().Database.ProviderName);
        Assert.False(scope.ServiceProvider.GetRequiredService<IOptions<OpenIddictEntityFrameworkCoreOptions>>().Value.DisableBulkOperations);
        var job = (IRecurringJob)scope.ServiceProvider.GetRequiredService(definition.JobType);
        var oldExpiredId = (await tokens.GetIdAsync(oldExpired))!;
        var youngExpiredId = (await tokens.GetIdAsync(youngExpired))!;
        var oldValidId = (await tokens.GetIdAsync(oldValid))!;
        var oldRevokedId = (await authorizations.GetIdAsync(oldRevoked))!;
        var youngRevokedId = (await authorizations.GetIdAsync(youngRevoked))!;
        var oldPermanentId = (await authorizations.GetIdAsync(oldPermanent))!;
        var oldAdHocId = (await authorizations.GetIdAsync(oldAdHoc))!;
        await job.ExecuteAsync(new RecurringJobContext(definition.Name, now), default);

        using var verify = host.Services.CreateScope();
        var storedTokens = verify.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var storedAuthorizations = verify.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        Assert.Null(await storedTokens.FindByIdAsync(oldExpiredId));
        Assert.Null(await storedAuthorizations.FindByIdAsync(oldRevokedId));
        Assert.Null(await storedAuthorizations.FindByIdAsync(oldAdHocId));
        Assert.NotNull(await storedTokens.FindByIdAsync(youngExpiredId));
        Assert.NotNull(await storedTokens.FindByIdAsync(oldValidId));
        Assert.NotNull(await storedAuthorizations.FindByIdAsync(youngRevokedId));
        Assert.NotNull(await storedAuthorizations.FindByIdAsync(oldPermanentId));
    }
}
#endif
