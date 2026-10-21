#if (RemoteTokenAuth)
using Leistd.Security.AspNetCore.Cookies;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
#if (!IncludeMultiTenancy)
using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.Security.Claims;
using Microsoft.EntityFrameworkCore;
#endif
using CompanyName.ProjectName.Application.Shared;
using CompanyName.ProjectName.Api.Auth.Sessions;
using Leistd.ServiceClient.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Validation;
using Leistd.Lock.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using System.Text.RegularExpressions;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>真实官方 OIDC/Cookie 处理器，只替换签发方的 HTTP 后端。</summary>
public sealed class ResourceBrowserSessionTests
{
#if (!IncludeMultiTenancy)
    [Theory]
    [InlineData("tenant")]
    [InlineData("malformed")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("conflicting")]
    public async Task A_cached_cookie_with_an_invalid_scope_is_rejected_before_projection(string kind)
    {
        using var factory = new ProjectWebApplicationFactory { UseProductionAuthentication = true };
        using var issuer = new Issuer();
        using var host = Host(factory, issuer);
        var cookie = await LoginAsync(host, issuer);
        var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthenticationSchemeNames.SessionCookie);
        var reference = options.TicketDataFormat.Unprotect(cookie.Split('=', 2)[1])!;
        var key = reference.Principal.Claims.Single().Value;
        var original = (await options.SessionStore!.RetrieveAsync(key))!;
        var tenant = Guid.NewGuid().ToString();
        var tenants = kind switch
        {
            "tenant" => new[] { tenant },
            "malformed" => new[] { "not-a-guid" },
            "empty" => new[] { "" },
            "duplicate" => new[] { tenant, tenant },
            "conflicting" => new[] { tenant, Guid.NewGuid().ToString() },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var claims = original.Principal.Claims.Concat(tenants.Select(value => new Claim(CustomClaimTypes.TenantId, value)));
        var changed = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationSchemeNames.SessionCookie)),
            original.Properties, original.AuthenticationScheme);
        await options.SessionStore.RenewAsync(key, changed);
        using var browser = Client(host, cookie);
        using var rejected = await browser.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        var subject = Guid.Parse(issuer.Subject);
        Assert.False(await db.Users.IgnoreQueryFilters().AnyAsync(user => user.Id == subject));
        await options.SessionStore.RenewAsync(key, original);
        using var accepted = await browser.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.True(await db.Users.IgnoreQueryFilters().AnyAsync(user => user.Id == subject));
    }
#endif

    [Fact]
    public async Task Official_code_flow_keeps_tokens_on_the_server_and_rejects_a_deleted_ticket()
    {
        using var factory = new ProjectWebApplicationFactory { UseProductionAuthentication = true };
        using var issuer = new Issuer();
        using var host = Host(factory, issuer);
        var cookie = await LoginAsync(host, issuer);
        var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthenticationSchemeNames.SessionCookie);
        var reference = options.TicketDataFormat.Unprotect(cookie.Split('=', 2)[1])!;
        Assert.Null(reference.Properties.GetTokenValue("access_token"));
        Assert.Null(reference.Properties.GetTokenValue("refresh_token"));
        Assert.Null(reference.Properties.GetTokenValue("id_token"));
        Assert.Single(reference.Principal.Claims);
        var key = reference.Principal.Claims.Single().Value;
        var ticket = await options.SessionStore!.RetrieveAsync(key);
        Assert.NotNull(ticket);
        Assert.NotNull(ticket.Properties.GetTokenValue("access_token"));
        Assert.Equal("refresh-1", ticket.Properties.GetTokenValue("refresh_token"));
        Assert.NotNull(ticket.Properties.GetTokenValue("id_token"));
        using var browser = Client(host, cookie);
        var me = await browser.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.Equal(issuer.Subject, me.GetProperty("id").GetString());
        Assert.Equal("oidc-user", me.GetProperty("username").GetString());
        // 签发方的 operator 声明不属于本资源服务的授权事实。
        Assert.Empty(me.GetProperty("roles").EnumerateArray());
        Assert.False(me.GetProperty("isSuperAdmin").GetBoolean());
        Assert.False(me.TryGetProperty("isActive", out _));
        Assert.False(me.TryGetProperty("creationTime", out _));
        browser.DefaultRequestHeaders.Authorization = new("Bearer", "invalid-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v1/auth/me")).StatusCode);
        browser.DefaultRequestHeaders.Authorization = null;
        await options.SessionStore.RemoveAsync(key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    /// <summary>会话主体只由访问令牌构建，登录与续期同源：访问令牌里的 acr、aud 等登录时就在，首次续期前后声明一致，不混入 id_token。</summary>
    /// <remarks>
    /// 官方处理器在 OnTokenValidated 之后还会对换上的主体执行默认 ClaimActions，删掉 acr、aud、iss、exp 等；
    /// 续期不经过它，这些声明首次续期后才出现。派生项目按访问令牌声明做判断（如近期认证）时，结果随会话是否续期过而变。
    /// </remarks>
    [Fact]
    public async Task The_session_principal_keeps_access_token_claims_from_login_through_refresh()
    {
        using var factory = new ProjectWebApplicationFactory { UseProductionAuthentication = true };
        using var issuer = new Issuer();
        using var host = Host(factory, issuer);
        var cookie = await LoginAsync(host, issuer);
        var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthenticationSchemeNames.SessionCookie);
        var key = options.TicketDataFormat.Unprotect(cookie.Split('=', 2)[1])!.Principal.Claims.Single().Value;

        var login = (await options.SessionStore!.RetrieveAsync(key))!.Principal;
        AssertBuiltFromAccessToken(login);
        var loginTypes = login.Claims.Select(claim => claim.Type).Distinct().Order().ToArray();

        var ticket = (await options.SessionStore.RetrieveAsync(key))!;
        ticket.Properties.UpdateTokenValue("expires_at", TimeProvider.System.GetUtcNow().AddSeconds(-1).ToString("o"));
        await options.SessionStore.RenewAsync(key, ticket);
        using var browser = Client(host, cookie);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(1, issuer.RefreshCount);

        var refreshed = (await options.SessionStore.RetrieveAsync(key))!.Principal;
        AssertBuiltFromAccessToken(refreshed);
        // 签发方夹具每次签同样结构的令牌：声明类型应一致（jti、时间等取值可以变）
        Assert.Equal(loginTypes, refreshed.Claims.Select(claim => claim.Type).Distinct().Order().ToArray());

        static void AssertBuiltFromAccessToken(ClaimsPrincipal principal)
        {
            Assert.True(principal.Identity?.IsAuthenticated);
            Assert.Equal(Issuer.AccessTokenAcr, Assert.Single(principal.FindAll("acr")).Value);
            Assert.Contains(principal.FindAll("aud"), claim => claim.Value == "resource-api");
            Assert.Null(principal.FindFirst("id_token_only"));
        }
    }

    /// <summary>
    /// 服务间调用只拿到经过认证的访问令牌：会话取服务端票据里的，合法 Bearer 取验证后的；
    /// Bearer 被拒时，OpenIddict 的失败结果里仍带着原令牌，也不能交出去。
    /// </summary>
    [Fact]
    public async Task User_access_token_comes_only_from_a_successful_authentication()
    {
        using var factory = new ProjectWebApplicationFactory { UseProductionAuthentication = true };
        using var issuer = new Issuer();
        using var host = Host(factory, issuer);
        var cookie = await LoginAsync(host, issuer);
        var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthenticationSchemeNames.SessionCookie);
        var key = options.TicketDataFormat.Unprotect(cookie.Split('=', 2)[1])!.Principal.Claims.Single().Value;
        var stored = (await options.SessionStore!.RetrieveAsync(key))!.Properties.GetTokenValue("access_token");
        Assert.NotNull(stored);

        Assert.Equal(stored, await AccessTokenAsync(host, "Cookie", cookie));
        Assert.Equal(stored, await AccessTokenAsync(host, "Authorization", "Bearer " + stored));
        Assert.Null(await AccessTokenAsync(host, "Authorization", "Bearer rejected-token"));
    }

    [Fact]
    public async Task Concurrent_requests_refresh_once_and_use_the_rotated_refresh_token()
    {
        using var factory = new ProjectWebApplicationFactory { UseProductionAuthentication = true };
        using var issuer = new Issuer();
        using var host = Host(factory, issuer);
        var cookie = await LoginAsync(host, issuer);
        var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthenticationSchemeNames.SessionCookie);
        var key = options.TicketDataFormat.Unprotect(cookie.Split('=', 2)[1])!.Principal.Claims.Single().Value;
        async Task ExpireAsync()
        {
            var ticket = (await options.SessionStore!.RetrieveAsync(key))!;
            ticket.Properties.UpdateTokenValue("expires_at", TimeProvider.System.GetUtcNow().AddSeconds(-1).ToString("o"));
            await options.SessionStore.RenewAsync(key, ticket);
        }
        await ExpireAsync();
        using var first = Client(host, cookie);
        using var second = Client(host, cookie);
        var responses = await Task.WhenAll(first.GetAsync("/api/v1/auth/me"), second.GetAsync("/api/v1/auth/me"));
        foreach (var response in responses) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, issuer.RefreshCount);
        var refreshed = (await options.SessionStore!.RetrieveAsync(key))!;
        Assert.Equal("refresh-2", refreshed.Properties.GetTokenValue("refresh_token"));
        await ExpireAsync();
        issuer.RejectRefresh = true;
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal("refresh-2", issuer.LastRefreshToken);
        Assert.Null(await options.SessionStore.RetrieveAsync(key));
    }

    [Fact]
    public async Task Missing_correlation_cannot_create_a_session_and_cross_origin_writes_are_rejected()
    {
        using var factory = new ProjectWebApplicationFactory { UseProductionAuthentication = true };
        using var issuer = new Issuer();
        using var host = Host(factory, issuer);
        using var browser = Client(host);
        var challenge = await browser.GetAsync("/api/v1/auth/login");
        var query = await FormPostAsync(challenge, Issuer.Address + "authorize");
        issuer.Nonce = query["nonce"];
        using var callback = await browser.PostAsync("/api/v1/auth/signin", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["code"] = "test-code", ["state"] = query["state"] }));
        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        Assert.False(callback.Headers.TryGetValues("Set-Cookie", out var values) && values.Any(value => value.StartsWith(ProjectWebApplicationFactory.SessionCookieName + "=")));
        var cookie = await LoginAsync(host, issuer);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Origin", "https://untrusted.test");
        using var forbidden = await browser.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("application/problem+json", forbidden.Content.Headers.ContentType?.MediaType);
        Assert.Equal(403, (await forbidden.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Sign_out_posts_the_identity_token_hint_to_the_issuer_instead_of_the_address_bar()
    {
        using var factory = new ProjectWebApplicationFactory { UseProductionAuthentication = true };
        using var issuer = new Issuer();
        using var host = Host(factory, issuer);
        var cookie = await LoginAsync(host, issuer);
        var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthenticationSchemeNames.SessionCookie);
        var key = options.TicketDataFormat.Unprotect(cookie.Split('=', 2)[1])!.Principal.Claims.Single().Value;
        var idToken = (await options.SessionStore!.RetrieveAsync(key))!.Properties.GetTokenValue("id_token");
        using var browser = Client(host, cookie);

        using var response = await browser.PostAsync("/api/v1/auth/logout", null);

        var form = await FormPostAsync(response, Issuer.Address + "logout");
        Assert.Equal(idToken, form["id_token_hint"]);
        Assert.Equal("resource-test", form["client_id"]);
        Assert.Equal("https://localhost/api/v1/auth/signout", form["post_logout_redirect_uri"]);
        Assert.False(string.IsNullOrEmpty(form["state"]));
        Assert.Null(await options.SessionStore.RetrieveAsync(key));
    }

    [Theory]
    [InlineData("state")]
    [InlineData("correlation")]
    [InlineData("token")]
    public async Task Production_remote_failure_rejects_invalid_protocol_messages(string failure)
    {
        using var factory = new ProjectWebApplicationFactory { UseProductionAuthentication = true };
        using var issuer = new Issuer { RejectCode = failure == "token" };
        using var host = Host(factory, issuer);
        using var browser = Client(host);
        using var challenge = await browser.GetAsync("/api/v1/auth/login");
        var query = await FormPostAsync(challenge, Issuer.Address + "authorize");
        issuer.Nonce = query["nonce"];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/signin")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            { ["code"] = "test-code", ["state"] = failure == "state" ? "invalid" : query["state"] })
        };
        var cookies = ExternalCookies(challenge);
        request.Headers.Add("Cookie", failure == "correlation" ? cookies.Replace("=N", "=wrong", StringComparison.Ordinal) : cookies);
        using var response = await browser.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var issued) && issued.Any(value => value.StartsWith(ProjectWebApplicationFactory.SessionCookieName + "=")));
    }

    [Fact]
    public async Task Valid_sessions_do_not_reacquire_the_refresh_lock_or_reread_the_ticket()
    {
        using var factory = new ProjectWebApplicationFactory { UseProductionAuthentication = true };
        using var issuer = new Issuer();
        using var host = Host(factory, issuer);
        var cookie = await LoginAsync(host, issuer);
        var probe = host.Services.GetRequiredService<RefreshLockProbe>();
        var cache = host.Services.GetRequiredService<TicketCacheProbe>();
        var initialReads = cache.TicketReads;
        using var browser = Client(host, cookie);
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => browser.GetAsync("/api/v1/auth/me")));
        foreach (var response in responses) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, probe.RefreshLocks);
        Assert.Equal(4, cache.TicketReads - initialReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_lock_failure_rejects_the_session_instead_of_returning_500(bool cleanupUnavailable)
    {
        using var factory = new ProjectWebApplicationFactory { UseProductionAuthentication = true };
        using var issuer = new Issuer();
        using var host = Host(factory, issuer);
        var cookie = await LoginAsync(host, issuer);
        var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthenticationSchemeNames.SessionCookie);
        var key = options.TicketDataFormat.Unprotect(cookie.Split('=', 2)[1])!.Principal.Claims.Single().Value;
        var ticket = (await options.SessionStore!.RetrieveAsync(key))!;
        ticket.Properties.UpdateTokenValue("expires_at", TimeProvider.System.GetUtcNow().AddSeconds(-1).ToString("o"));
        await options.SessionStore.RenewAsync(key, ticket);
        host.Services.GetRequiredService<RefreshLockProbe>().Unavailable = true;
        host.Services.GetRequiredService<RefreshLockProbe>().AllLocksUnavailable = cleanupUnavailable;
        using var browser = Client(host, cookie);
        using var response = await browser.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith(ProjectWebApplicationFactory.SessionCookieName + "=;") && value.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
        if (!cleanupUnavailable) Assert.Null(await options.SessionStore.RetrieveAsync(key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_old_request_cannot_delete_a_new_sign_in(bool refreshFailure)
    {
        using var factory = new ProjectWebApplicationFactory { UseProductionAuthentication = true };
        using var issuer = new Issuer { RejectRefresh = true };
        using var host = Host(factory, issuer);
        var cookie = await LoginAsync(host, issuer);
        using var oldScope = host.Services.CreateScope();
        using var newScope = host.Services.CreateScope();
        DefaultHttpContext Context(IServiceProvider services)
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("localhost");
            context.Request.Headers.Cookie = cookie;
            return context;
        }
        var oldContext = Context(oldScope.ServiceProvider);
        var newContext = Context(newScope.ServiceProvider);
        var oldAuthentication = await oldContext.AuthenticateAsync(AuthenticationSchemeNames.SessionCookie);
        var newAuthentication = await newContext.AuthenticateAsync(AuthenticationSchemeNames.SessionCookie);
        Assert.True(oldAuthentication.Succeeded);
        Assert.True(newAuthentication.Succeeded);
        var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthenticationSchemeNames.SessionCookie);
        CookieValidatePrincipalContext? validation = null;
        Task? refresh = null;
        if (refreshFailure)
        {
            oldAuthentication.Properties!.UpdateTokenValue("expires_at", TimeProvider.System.GetUtcNow().AddSeconds(-1).ToString("o"));
            var oldKey = oldAuthentication.Properties.Items[DistributedTicketStore.TicketKeyProperty]!;
            await options.SessionStore!.RenewAsync(oldKey, oldAuthentication.Ticket!);
            issuer.RefreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            issuer.ContinueRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
            validation = new CookieValidatePrincipalContext(oldContext,
                new AuthenticationScheme(AuthenticationSchemeNames.SessionCookie, null, typeof(CookieAuthenticationHandler)), options, oldAuthentication.Ticket!);
            refresh = oldScope.ServiceProvider.GetRequiredService<ResourceSessionRefresher>().ValidateAsync(validation);
            await issuer.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await newContext.SignInAsync(AuthenticationSchemeNames.SessionCookie, newAuthentication.Principal!, newAuthentication.Properties);
        var replacement = newContext.Response.Headers.SetCookie.Single(value => value!.StartsWith(ProjectWebApplicationFactory.SessionCookieName + "="))!.Split(';')[0];
        var reference = options.TicketDataFormat.Unprotect(replacement.Split('=', 2)[1])!;
        var key = reference.Principal.Claims.Single().Value;
        if (refreshFailure)
        {
            issuer.ContinueRefresh!.SetResult(true);
            await refresh!.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(validation!.Principal);
            Assert.Equal(1, issuer.RefreshCount);
        }
        else await oldContext.SignOutAsync(AuthenticationSchemeNames.SessionCookie);
        Assert.NotNull(await options.SessionStore!.RetrieveAsync(key));
        using var browser = Client(host, replacement);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/auth/me")).StatusCode);
        using var stale = Client(host, cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stale.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    private static string ExternalCookies(HttpResponseMessage response) =>
        string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));

    /// <summary>授权与退出请求以自动提交的表单 POST 发往签发方（官方 FormPost）：参数只在表单里，不在跳转地址上。</summary>
    private static async Task<Dictionary<string, string>> FormPostAsync(HttpResponseMessage response, string action)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync();
        var form = Regex.Match(html, "<form(?=[^>]*method=\"post\")[^>]*action=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);
        Assert.True(form.Success, html);
        Assert.Equal(action, WebUtility.HtmlDecode(form.Groups[1].Value));
        return Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\"")
            .ToDictionary(match => WebUtility.HtmlDecode(match.Groups[1].Value), match => WebUtility.HtmlDecode(match.Groups[2].Value));
    }

    private static WebApplicationFactory<Program> Host(ProjectWebApplicationFactory factory, Issuer issuer) =>
        factory.WithWebHostBuilder(builder => builder.UseSetting("Authentication:Audience", "resource-api").ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, AccessTokenProbe>();
            services.AddSingleton<RefreshLockProbe>();
            services.AddSingleton<IDistributedLock>(provider => provider.GetRequiredService<RefreshLockProbe>());
            services.AddSingleton<TicketCacheProbe>();
            services.AddSingleton<IDistributedCache>(provider => provider.GetRequiredService<TicketCacheProbe>());
            services.Configure<OpenIdConnectOptions>(AuthenticationSchemeNames.OpenIdConnect, options =>
            {
                options.BackchannelHttpHandler = issuer;
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = Issuer.Address, AuthorizationEndpoint = Issuer.Address + "authorize", TokenEndpoint = Issuer.Address + "token",
                    EndSessionEndpoint = Issuer.Address + "logout"
                };
                options.Configuration.SigningKeys.Add(issuer.Key);
            });
            services.Configure<OpenIddictValidationOptions>(options =>
            {
                options.Configuration = new OpenIddictConfiguration { Issuer = new Uri(Issuer.Address) };
                options.Configuration.SigningKeys.Add(issuer.Key);
            });
        }));

    // 经测试分支走一次真实请求：OpenIddict 验证要求认证中间件先为请求建好上下文，脱离管道的 HttpContext 认证不了
    private static async Task<string?> AccessTokenAsync(WebApplicationFactory<Program> host, string header, string value)
    {
        using var client = Client(host);
        using var request = new HttpRequestMessage(HttpMethod.Get, AccessTokenProbe.Path);
        request.Headers.TryAddWithoutValidation(header, value);
        using var response = await client.SendAsync(request);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.Content.ReadAsStringAsync();
    }

    /// <summary>在生产管道前加一个测试分支：分支内先认证，再调用宿主登记的用户访问令牌读取器。</summary>
    private sealed class AccessTokenProbe : IStartupFilter
    {
        public const string Path = "/test/user-access-token";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map(Path, branch =>
            {
                branch.UseAuthentication();
                branch.Run(async context =>
                {
                    var token = await context.RequestServices.GetRequiredService<IUserAccessTokenAccessor>().GetAccessTokenAsync();
                    if (token is null) context.Response.StatusCode = StatusCodes.Status204NoContent;
                    else await context.Response.WriteAsync(token);
                });
            });
            next(app);
        };
    }

    private static HttpClient Client(WebApplicationFactory<Program> host, string? cookie = null)
    {
        var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        client.BaseAddress = new Uri("https://localhost");
        if (cookie is not null) client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static async Task<string> LoginAsync(WebApplicationFactory<Program> host, Issuer issuer)
    {
        using var browser = Client(host);
        using var challenge = await browser.GetAsync("/api/v1/auth/login?returnUrl=/workspace");
        var query = await FormPostAsync(challenge, Issuer.Address + "authorize");
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        issuer.Nonce = query["nonce"];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/signin")
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "test-code", ["state"] = query["state"] }) };
        request.Headers.Add("Cookie", string.Join("; ", challenge.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0])));
        using var callback = await browser.SendAsync(request);
        Assert.True(callback.StatusCode == HttpStatusCode.Found, await callback.Content.ReadAsStringAsync());
        Assert.Equal("/workspace", callback.Headers.Location!.OriginalString);
        return ProjectWebApplicationFactory.AssertSessionCookieContract(callback);
    }

    private sealed class RefreshLockProbe(ILocalLock inner) : IDistributedLock
    {
        private int _refreshLocks;
        public int RefreshLocks => _refreshLocks;
        public bool Unavailable { get; set; }
        public bool AllLocksUnavailable { get; set; }
        public Task<ILockHandle> LockAsync(string key, CancellationToken cancellationToken = default)
        {
            if (key.EndsWith(":refresh", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _refreshLocks);
                if (Unavailable) throw new InvalidOperationException("Refresh lock unavailable.");
            }
            if (AllLocksUnavailable) throw new InvalidOperationException("Lock service unavailable.");
            return inner.LockAsync(key, cancellationToken);
        }
        public Task<ILockHandle?> TryLockAsync(string key, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            inner.TryLockAsync(key, timeout, cancellationToken);
    }

    private sealed class TicketCacheProbe : IDistributedCache
    {
        private readonly MemoryDistributedCache _inner = new(
            Options.Create(new MemoryDistributedCacheOptions()));
        private int _ticketReads;
        public int TicketReads => _ticketReads;
        public byte[]? Get(string key) => _inner.Get(key);
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            if (key.StartsWith("CompanyName.ProjectName:AuthTicket:", StringComparison.Ordinal)) Interlocked.Increment(ref _ticketReads);
            return _inner.GetAsync(key, token);
        }
        public void Refresh(string key) => _inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);
        public void Remove(string key) => _inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _inner.Set(key, value, options);
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
            _inner.SetAsync(key, value, options, token);
    }

    private sealed class Issuer : HttpMessageHandler
    {
        public const string Address = "https://identity.test/";
        public const string AccessTokenAcr = "access-token-acr";
        public RsaSecurityKey Key { get; } = new(RSA.Create(2048)) { KeyId = "oidc-test" };
        public string Subject { get; } = Guid.NewGuid().ToString();
        public string Nonce { get; set; } = "";
        public int RefreshCount { get; private set; }
        public string? LastRefreshToken { get; private set; }
        public bool RejectRefresh { get; set; }
        public bool RejectCode { get; set; }
        public TaskCompletionSource<bool>? RefreshStarted { get; set; }
        public TaskCompletionSource<bool>? ContinueRefresh { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            var refresh = form["grant_type"].ToString() == "refresh_token";
            if (refresh)
            {
                RefreshCount++;
                if (RefreshStarted is not null)
                {
                    RefreshStarted.SetResult(true);
                    await ContinueRefresh!.Task.WaitAsync(cancellationToken);
                }
                LastRefreshToken = form["refresh_token"].ToString();
                if (RejectRefresh) return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "invalid_grant" }) };
            }
            else
            {
                Assert.False(string.IsNullOrEmpty(form["code_verifier"]));
                if (RejectCode) return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "invalid_grant" }) };
            }
            string Token(string audience, string type, Dictionary<string, object> claims) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Address, Audience = audience, Claims = claims, TokenType = type,
                IssuedAt = TimeProvider.System.GetUtcNow().UtcDateTime, NotBefore = TimeProvider.System.GetUtcNow().AddSeconds(-5).UtcDateTime,
                Expires = TimeProvider.System.GetUtcNow().AddMinutes(10).UtcDateTime, SigningCredentials = new(Key, SecurityAlgorithms.RsaSha256)
            });
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
            {
                access_token = Token("resource-api", "at+jwt", new()
                { ["sub"] = Subject, ["preferred_username"] = "oidc-user", ["email"] = "user@example.test", ["role"] = "operator", ["acr"] = AccessTokenAcr, ["jti"] = Guid.NewGuid().ToString() }),
                // id_token 带一个与访问令牌不同的 acr 和一个专属声明：会话里出现它们，就说明主体混入了 id_token
                id_token = Token("resource-test", "JWT", new() { ["sub"] = Subject, ["nonce"] = Nonce, ["acr"] = "id-token-acr", ["id_token_only"] = "x" }),
                refresh_token = refresh ? "refresh-2" : "refresh-1", token_type = "Bearer", expires_in = 600
            }) };
        }
        protected override void Dispose(bool disposing) { if (disposing) Key.Rsa.Dispose(); base.Dispose(disposing); }
    }
}
#endif
