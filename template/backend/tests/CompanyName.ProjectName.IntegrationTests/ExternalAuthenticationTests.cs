#if (ExternalLogin)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using CompanyName.ProjectName.Api.Auth.Authentication;
using CompanyName.ProjectName.Domain.Users.DomainServices;
using Leistd.Security.OneTimeCodes;
using CompanyName.ProjectName.Domain.Shared.Security.PasswordHash;
using CompanyName.ProjectName.Domain.Auth.Abstractions;
using CompanyName.ProjectName.Domain.Auth.Entities;
using CompanyName.ProjectName.Domain.Auth.Errors;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.Data.Filters;
using Leistd.Ddd.Domain.Repositories;
using Microsoft.Extensions.Logging;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Leistd.MultiTenancy.Context;
#if (IncludeMultiTenancy)
using Leistd.MultiTenancy.AspNetCore.Options;
using Leistd.UnitOfWork;
#endif
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using CompanyName.ProjectName.Application.Shared;
#if (OpenIddictServer)
using OpenIddict.Abstractions;
using CompanyName.ProjectName.Application.Auth.Options;
#endif
#if (OpenIddictServer)
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
#endif

namespace CompanyName.ProjectName.IntegrationTests;

public sealed class ExternalAuthenticationTests
{
    [Theory]
    [InlineData("github")]
    [InlineData("google")]
    public async Task The_official_handler_sends_pkce_and_keeps_tokens_out_of_browser_cookies(string provider)
    {
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel();
        using var host = backchannel.CreateHost(factory);
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        var flow = await ExternalOAuthBackchannel.StartAsync(client, provider);
        Assert.False(string.IsNullOrEmpty(backchannel.CodeVerifier));
        client.DefaultRequestHeaders.Add("Cookie", flow.Cookie);
        using var complete = await client.PostAsJsonAsync($"/api/v1/external-auth/{provider}/complete", new { });
        Assert.True(complete.IsSuccessStatusCode, await complete.Content.ReadAsStringAsync());
        var setCookie = complete.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(ProjectWebApplicationFactory.SessionCookieName + "=", StringComparison.Ordinal));
        var value = Uri.UnescapeDataString(setCookie.Split(';', 2)[0].Split('=', 2)[1]);
        var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthenticationSchemeNames.SessionCookie);
        var reference = options.TicketDataFormat.Unprotect(value);
        Assert.NotNull(reference);
        Assert.Empty(reference.Properties.GetTokens());
        Assert.Single(reference.Principal.Claims);
        var store = options.SessionStore!;
        var key = reference.Principal.Claims.Single().Value;
        Assert.NotNull(await store.RetrieveAsync(key));
        await store.RemoveAsync(key);
        using var stale = ProjectWebApplicationFactory.CreateProjectClient(host);
        stale.DefaultRequestHeaders.Add("Cookie", setCookie.Split(';', 2)[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stale.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData("google", false)]
    [InlineData("github", false)]
    [InlineData("github", true)]
    public async Task Official_userinfo_mapping_does_not_link_an_unverified_email(string provider, bool emailUnavailable)
    {
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel
        {
            User = new ExternalUserInfo { ProviderId = "unverified-mapping", ProviderAccountLabel = "unverified", SuggestedUsername = "unverified",
                Email = "admin@companyname-projectname.com", EmailVerified = emailUnavailable }, EmailsUnavailable = emailUnavailable
        };
        using var host = backchannel.CreateHost(factory);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            var admin = await db.Set<User>().SingleAsync(user => user.Username == "admin");
            admin.ConfirmEmail();
            await db.SaveChangesAsync();
        }
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        var flow = await ExternalOAuthBackchannel.StartAsync(client, provider);
        client.DefaultRequestHeaders.Add("Cookie", flow.Cookie);
        using var response = await client.PostAsJsonAsync($"/api/v1/external-auth/{provider}/complete", new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ExternalAuth:AccountExistsSignInToLink", problem.GetProperty("code").GetString());
        // 提示用官方 scheme 的显示名，而不是内部标识（github → GitHub）。
        Assert.Contains(provider == "github" ? "GitHub" : "Google", problem.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) &&
            cookies.Any(value => value.StartsWith(ProjectWebApplicationFactory.SessionCookieName + "=", StringComparison.Ordinal)));

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GitHub_links_by_the_verified_primary_email_rather_than_the_public_profile_email(bool publicEmailHidden)
    {
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel
        {
            User = new ExternalUserInfo { ProviderId = "primary-mapping", ProviderAccountLabel = "primary", SuggestedUsername = "primary",
                Email = "public@elsewhere.test", EmailVerified = true },
            PrimaryEmail = "admin@companyname-projectname.com", PublicEmailHidden = publicEmailHidden
        };
        using var host = backchannel.CreateHost(factory);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            (await db.Set<User>().SingleAsync(user => user.Username == "admin")).ConfirmEmail();
            await db.SaveChangesAsync();
        }
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        var flow = await ExternalOAuthBackchannel.StartAsync(client);
        client.DefaultRequestHeaders.Add("Cookie", flow.Cookie);
        using var response = await client.PostAsJsonAsync("/api/v1/external-auth/github/complete", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var signedIn = ProjectWebApplicationFactory.CreateProjectClient(host);
        signedIn.DefaultRequestHeaders.Add("Cookie", ExternalOAuthBackchannel.Cookies(response));
        var me = await signedIn.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.Equal("admin", me.GetProperty("username").GetString());
    }

    [Fact]
    public async Task GitHub_sign_in_without_a_public_email_survives_an_unavailable_email_endpoint()
    {
        // 处理器自带的邮箱补取失败即中断登录；关掉它之后，邮箱接口故障只让本次不按邮箱关联。
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel
        {
            User = new ExternalUserInfo { ProviderId = "privateuser", ProviderAccountLabel = "privateuser", SuggestedUsername = "privateuser",
                Email = "privateuser@elsewhere.test", EmailVerified = true },
            PublicEmailHidden = true, EmailsUnavailable = true
        };
        using var host = backchannel.CreateHost(factory);
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        var flow = await ExternalOAuthBackchannel.StartAsync(client);
        client.DefaultRequestHeaders.Add("Cookie", flow.Cookie);
        using var response = await client.PostAsJsonAsync("/api/v1/external-auth/github/complete", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var signedIn = ProjectWebApplicationFactory.CreateProjectClient(host);
        signedIn.DefaultRequestHeaders.Add("Cookie", ExternalOAuthBackchannel.Cookies(response));
        var me = await signedIn.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.Equal("privateuser", me.GetProperty("username").GetString());
        // 邮箱接口不可用：拿不到这个不公开的地址，账号只有系统占位邮箱。
        Assert.NotEqual("privateuser@elsewhere.test", me.TryGetProperty("email", out var email) ? email.GetString() : null);
    }

    [Fact]
    public async Task The_provider_directory_is_public_and_empty_without_configured_providers()
    {
        using var factory = new ProjectWebApplicationFactory();
        using var client = ProjectWebApplicationFactory.CreateProjectClient(factory);
        var directory = await client.GetFromJsonAsync<JsonElement>("/api/v1/external-auth/providers");
        Assert.Empty(directory.GetProperty("providers").EnumerateArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_missing_or_mismatched_correlation_is_rejected(bool mismatch)
    {
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel();
        using var host = backchannel.CreateHost(factory);
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var challenge = await client.GetAsync("/api/v1/external-auth/github/challenge");
        var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"].ToString();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/external-auth/github/signin?code=code&state={Uri.EscapeDataString(state)}");
        if (mismatch) request.Headers.Add("Cookie", ExternalOAuthBackchannel.Cookies(challenge).Replace("=N", "=wrong", StringComparison.Ordinal));
        using var response = await client.SendAsync(request);
        // 浏览器整页导航：回到前端回调页并带原因码，不签发外部票据，也不交换授权码。
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/auth/external-callback/github?intent=login&error=failed", response.Headers.Location!.OriginalString);
        Assert.DoesNotContain(".External=", ExternalOAuthBackchannel.Cookies(response), StringComparison.Ordinal);
        Assert.Null(backchannel.CodeVerifier);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("link")]
    public async Task Cancelling_at_the_provider_returns_to_the_callback_page_without_a_ticket(string intent)
    {
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel();
        using var host = backchannel.CreateHost(factory);
        HttpClient client;
        AuthenticatedSession? session = null;
        if (intent == "link")
        {
            session = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
            client = session.Client;
        }
        else client = ProjectWebApplicationFactory.CreateProjectClient(host);
        using (session)
        {
            using var challenge = await client.GetAsync($"/api/v1/external-auth/github/{(intent == "link" ? "link/" : "")}challenge");
            var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"].ToString();
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/v1/external-auth/github/signin?error=access_denied&state={Uri.EscapeDataString(state)}");
            request.Headers.Add("Cookie", string.Join("; ", new[] { session?.Cookie ?? "", ExternalOAuthBackchannel.Cookies(challenge) }.Where(value => value.Length > 0)));
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Equal($"/auth/external-callback/github?intent={intent}&error=cancelled", response.Headers.Location!.OriginalString);
            Assert.DoesNotContain(".External=", ExternalOAuthBackchannel.Cookies(response), StringComparison.Ordinal);
            Assert.Null(backchannel.CodeVerifier);
        }
    }

    [Fact]
    public async Task Successful_complete_consumes_the_external_ticket_and_rejects_a_copied_cookie()
    {
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel();
        using var host = backchannel.CreateHost(factory);
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        var flow = await ExternalOAuthBackchannel.StartAsync(client);
        client.DefaultRequestHeaders.Add("Cookie", flow.Cookie);
        using var success = await client.PostAsJsonAsync("/api/v1/external-auth/github/complete", new { });
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        using var copy = ProjectWebApplicationFactory.CreateProjectClient(host);
        copy.DefaultRequestHeaders.Add("Cookie", flow.Cookie);
        using var replay = await copy.PostAsJsonAsync("/api/v1/external-auth/github/complete", new { });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task Concurrent_completions_of_one_external_ticket_succeed_only_once()
    {
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel();
        using var host = backchannel.CreateHost(factory);
        using var starter = ProjectWebApplicationFactory.CreateProjectClient(host);
        var flow = await ExternalOAuthBackchannel.StartAsync(starter);
        var clients = Enumerable.Range(0, 4).Select(_ =>
        {
            var client = ProjectWebApplicationFactory.CreateProjectClient(host);
            client.DefaultRequestHeaders.Add("Cookie", flow.Cookie);
            return client;
        }).ToList();

        var responses = await Task.WhenAll(clients.Select(client =>
            client.PostAsJsonAsync("/api/v1/external-auth/github/complete", new { })));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(response => response.StatusCode != HttpStatusCode.OK),
            response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
        foreach (var response in responses) response.Dispose();
        foreach (var client in clients) client.Dispose();
    }

    [Fact]
    public async Task An_expired_external_ticket_cannot_complete_a_session()
    {
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel();
        using var host = backchannel.CreateHost(factory);
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        var flow = await ExternalOAuthBackchannel.StartAsync(client);
        var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthenticationSchemeNames.ExternalCookie);
        var cookie = flow.Cookie.Split("; ", StringSplitOptions.RemoveEmptyEntries).Single(value => value.StartsWith("__Host-CompanyName.ProjectName.External="));
        var key = options.TicketDataFormat.Unprotect(cookie.Split('=', 2)[1])!.Principal.Claims.Single().Value;
        var ticket = (await options.SessionStore!.RetrieveAsync(key))!;
        ticket.Properties.ExpiresUtc = TimeProvider.System.GetUtcNow().AddSeconds(-1);
        await options.SessionStore.RenewAsync(key, ticket);
        client.DefaultRequestHeaders.Add("Cookie", flow.Cookie);
        using var response = await client.PostAsJsonAsync("/api/v1/external-auth/github/complete", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var values) && values.Any(value => value.StartsWith(ProjectWebApplicationFactory.SessionCookieName + "=")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("None")]
    public async Task Protocol_correlation_keeps_the_official_policy_independent_of_the_session(string? policy)
    {
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel();
        using var host = backchannel.CreateHost(factory).WithWebHostBuilder(builder => builder.UseSetting("SessionCookie:SameSite", policy));
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var challenge = await client.GetAsync("/api/v1/external-auth/github/challenge");
        var cookie = challenge.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(".AspNetCore.Correlation", StringComparison.Ordinal));
        Assert.Contains("samesite=none", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task External_sign_in_with_two_factor_does_not_issue_a_final_session_before_the_second_step()
    {
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel { User = new ExternalUserInfo
        {
            ProviderId = "mfa-admin", ProviderAccountLabel = "admin", Email = "admin@companyname-projectname.com", EmailVerified = true
        } };
        using var host = backchannel.CreateHost(factory);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            var admin = await db.Set<User>().SingleAsync(user => user.Username == "admin");
            admin.ConfirmEmail();
            var secret = Enumerable.Range(1, 20).Select(value => (byte)value).ToArray();
            admin.EnableTwoFactor(scope.ServiceProvider.GetRequiredService<TwoFactorDomainService>().ProtectSecret(secret), [], 1);
            await db.SaveChangesAsync();
        }
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        var flow = await ExternalOAuthBackchannel.StartAsync(client, returnUrl: "/connect/authorize?client_id=resource");
        client.DefaultRequestHeaders.Add("Cookie", flow.Cookie);
        using var complete = await client.PostAsJsonAsync("/api/v1/external-auth/github/complete", new { });
        Assert.True(complete.IsSuccessStatusCode, await complete.Content.ReadAsStringAsync());
        using var result = JsonDocument.Parse(await complete.Content.ReadAsStringAsync());
        Assert.True(result.RootElement.GetProperty("requiresTwoFactor").GetBoolean());
        Assert.False(string.IsNullOrEmpty(result.RootElement.GetProperty("twoFactorToken").GetString()));
        Assert.Equal("/connect/authorize?client_id=resource", result.RootElement.GetProperty("returnUrl").GetString());
        var code = Totp.ComputeCode(Enumerable.Range(1, 20).Select(value => (byte)value).ToArray(), Totp.TimeStepAt(DateTimeOffset.UtcNow));
        using var secondStep = await client.PostAsJsonAsync("/api/v1/auth/two-factor",
            new { Token = result.RootElement.GetProperty("twoFactorToken").GetString(), Code = code });
        Assert.True(secondStep.IsSuccessStatusCode, await secondStep.Content.ReadAsStringAsync());
        Assert.Contains(secondStep.Headers.GetValues("Set-Cookie"), value => value.StartsWith(ProjectWebApplicationFactory.SessionCookieName + "=", StringComparison.Ordinal));
        Assert.DoesNotContain(complete.Headers.GetValues("Set-Cookie"), value => value.StartsWith(ProjectWebApplicationFactory.SessionCookieName + "=", StringComparison.Ordinal));
    }

#if (IncludeMultiTenancy)
    [Fact]
    public async Task Tenant_external_login_cookie_preserves_the_tenant_without_a_request_header()
    {
        const string tenantName = "external-tenant";
        const string tenantEmail = "admin@external-tenant.example.com";
        using var factory = new ProjectWebApplicationFactory();
        using var host = CreateExternalAuthHost(factory, new ExternalUserInfo
        {
            ProviderId = "tenant-admin",
            ProviderAccountLabel = "tenant-admin",
            SuggestedUsername = "tenant-admin",
            Email = tenantEmail,
            EmailVerified = true
        });
        using var hostAdmin = await ProjectWebApplicationFactory.LoginAsync(
            host,
            "admin",
            ProjectWebApplicationFactory.TestAdminPassword);

        var createTenant = await hostAdmin.Client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = tenantName,
            DisplayName = "External Tenant",
            AdminEmail = tenantEmail,
            AdminPassword = "Tenant@123456"
        });
        Assert.Equal(HttpStatusCode.OK, createTenant.StatusCode);
        var tenant = await createTenant.Content.ReadFromJsonAsync<TenantIdResponse>();
        Assert.NotNull(tenant);

        // 两边邮箱都已验证才按邮箱关联到租户管理员
        await using (var scope = host.Services.CreateAsyncScope())
        using (scope.ServiceProvider.GetRequiredService<ICurrentTenant>().Change(tenant.Id))
        {
            using var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true);
            var userRepository = scope.ServiceProvider.GetRequiredService<IRepository<User, Guid>>();
            var admin = await userRepository.GetOneAsync(user => user.Email == tenantEmail);
            admin!.ConfirmEmail();
            await userRepository.UpdateAsync(admin);
            await unitOfWork.CompleteAsync();
        }

        using var externalClient = ProjectWebApplicationFactory.CreateProjectClient(host);
        externalClient.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, tenant.Id.ToString());
        var challenge = await ExternalOAuthBackchannel.StartAsync(externalClient);
        externalClient.DefaultRequestHeaders.Add("Cookie", challenge.Cookie);
        var callback = await externalClient.PostAsJsonAsync(
            "/api/v1/external-auth/github/complete",
            new { });
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);

        var authCookie = callback.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(ProjectWebApplicationFactory.SessionCookieName + "=", StringComparison.Ordinal))
            .Split(';', 2)[0];
        using var sessionClient = ProjectWebApplicationFactory.CreateProjectClient(host);
        sessionClient.DefaultRequestHeaders.Add("Cookie", authCookie);

        var users = await sessionClient.GetFromJsonAsync<UserPageResponse>("/api/v1/users?offset=0&limit=10");
        Assert.NotNull(users);
        Assert.Single(users.Items);
        Assert.Equal(tenantEmail, users.Items[0].Email);
    }
#else
    [Fact]
    public async Task Host_external_login_ignores_anonymous_tenant_hints_and_preserves_the_host_session()
    {
        const string username = "host_external_user";
        using var factory = new ProjectWebApplicationFactory();
        using var host = CreateExternalAuthHost(factory, new ExternalUserInfo
        {
            ProviderId = username,
            ProviderAccountLabel = username,
            SuggestedUsername = username,
            Email = "host-external-user@provider.test",
            EmailVerified = true
        });
        using var externalClient = ProjectWebApplicationFactory.CreateProjectClient(host);
        // 单租户不解析匿名租户线索，外部身份完成后仍只能取得宿主会话。
        externalClient.DefaultRequestHeaders.Add("X-Tenant", Guid.NewGuid().ToString());
        var challenge = await ExternalOAuthBackchannel.StartAsync(externalClient);
        externalClient.DefaultRequestHeaders.Add("Cookie", challenge.Cookie);
        using var callback = await externalClient.PostAsJsonAsync("/api/v1/external-auth/github/complete", new { });
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);

        using var sessionClient = ProjectWebApplicationFactory.CreateProjectClient(host);
        sessionClient.DefaultRequestHeaders.Add("Cookie", ExternalOAuthBackchannel.Cookies(callback));
        var me = await sessionClient.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.Equal(username, me.GetProperty("username").GetString());
        await using var scope = host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        var user = await dbContext.Users.IgnoreQueryFilters().SingleAsync(user => user.Id == me.GetProperty("id").GetGuid());
        Assert.Null(user.TenantId);
    }
#endif

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disabled_or_locked_accounts_cannot_sign_in_through_an_external_provider(bool locked)
    {
        const string adminEmail = "admin@companyname-projectname.com";
        using var factory = new ProjectWebApplicationFactory();
        using var host = CreateExternalAuthHost(factory, new ExternalUserInfo
        {
            ProviderId = locked ? "locked-admin" : "disabled-admin",
            ProviderAccountLabel = "admin",
            SuggestedUsername = "admin",
            Email = adminEmail,
            EmailVerified = true
        });

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            var admin = await dbContext.Set<User>().SingleAsync(user => user.Email == adminEmail);
            // 两边邮箱都已验证才按邮箱关联到这个账号；否则被拒的原因是"邮箱已被占用"，不是停用或锁定
            admin.ConfirmEmail();
            if (locked)
            {
                admin.Lock();
            }
            else
            {
                admin.Disable();
            }

            await dbContext.SaveChangesAsync();
        }

        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        var challenge = await ExternalOAuthBackchannel.StartAsync(client);
        client.DefaultRequestHeaders.Add("Cookie", challenge.Cookie);
        using var callback = await client.PostAsJsonAsync(
            "/api/v1/external-auth/github/complete",
            new { });

        Assert.Equal(HttpStatusCode.Unauthorized, callback.StatusCode);
        // 票据在账号政策之前已被消费：被拒之后同一张票据不能再提交
        using var retry = await client.PostAsJsonAsync("/api/v1/external-auth/github/complete", new { });
        Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
    }


    [Theory]
    [InlineData("//evil.test")]
    [InlineData("/\\evil.test")]
    [InlineData("https://evil.test")]
    public async Task External_challenge_rejects_non_local_return_addresses(string returnUrl)
    {
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel();
        using var host = backchannel.CreateHost(factory);
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var response = await client.GetAsync("/api/v1/external-auth/github/challenge?returnUrl=" + Uri.EscapeDataString(returnUrl));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task External_login_restores_the_return_address_from_protected_properties()
    {
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel();
        using var host = backchannel.CreateHost(factory);
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        const string returnUrl = "/connect/authorize?client_id=resource&state=original";
        var flow = await ExternalOAuthBackchannel.StartAsync(client, returnUrl: returnUrl);
        client.DefaultRequestHeaders.Add("Cookie", flow.Cookie);
        using var response = await client.PostAsJsonAsync("/api/v1/external-auth/github/complete?returnUrl=https://evil.test", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(returnUrl, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("returnUrl").GetString());
    }

    [Fact]
    public async Task Explicit_official_schemes_extend_the_provider_directory_and_internal_schemes_are_excluded()
    {
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel();
        using var host = backchannel.CreateHost(factory).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddAuthentication()
                .AddCookie(AuthenticationSchemeNames.ExternalProviderPrefix + "local")
                .AddOAuth(AuthenticationSchemeNames.ExternalProviderPrefix + "custom", options =>
                {
                    options.ClientId = "custom-client";
                    options.ClientSecret = "custom-secret";
                    options.SignInScheme = AuthenticationSchemeNames.ExternalCookie;
                    options.CallbackPath = "/api/v1/external-auth/custom/signin";
                    options.AuthorizationEndpoint = "https://custom.test/authorize";
                    options.TokenEndpoint = "https://custom.test/token";
                    options.UserInformationEndpoint = "https://custom.test/user";
                    options.UsePkce = true;
                    options.BackchannelHttpHandler = backchannel;
                    options.Events.OnCreatingTicket = context =>
                    {
                        context.Properties.Items[ExternalAuthenticationExtensions.UserInfoKey] = JsonSerializer.Serialize(backchannel.User);
                        return Task.CompletedTask;
                    };
                })));
        using var anonymous = ProjectWebApplicationFactory.CreateProjectClient(host);
        var directory = await anonymous.GetFromJsonAsync<JsonElement>("/api/v1/external-auth/providers");
        Assert.Equal(new[] { "custom", "github", "google" }, directory.GetProperty("providers").EnumerateArray()
            .Select(item => item.GetString()).ToArray());
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var links = await admin.Client.GetFromJsonAsync<JsonElement>("/api/v1/external-auth/links");
        Assert.Equal(new[] { "custom", "github", "google" }, links.GetProperty("providers").EnumerateArray()
            .Select(item => item.GetProperty("provider").GetString()).Order().ToArray());
        foreach (var provider in new[] { "local", "MyProjectCookie", "MyProjectSmart", "MyProjectExternal" })
        {
            using var rejected = await admin.Client.GetAsync($"/api/v1/external-auth/{provider}/challenge");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("ExternalAuth:ProviderNotConfigured", problem.GetProperty("code").GetString());
            Assert.DoesNotContain("{", problem.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        }
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        var flow = await ExternalOAuthBackchannel.StartAsync(client, "custom");
        client.DefaultRequestHeaders.Add("Cookie", flow.Cookie);
        using var complete = await client.PostAsJsonAsync("/api/v1/external-auth/custom/complete", new { });
        Assert.True(complete.IsSuccessStatusCode, await complete.Content.ReadAsStringAsync());
    }

#if (OpenIddictServer)
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_authorization_request_continues_through_external_login_and_optional_two_factor(bool twoFactor)
    {
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel();
        using var host = backchannel.CreateHost(factory);
        var secret = Enumerable.Range(1, 20).Select(value => (byte)value).ToArray();
        if (twoFactor)
        {
            backchannel.User = new ExternalUserInfo { ProviderId = "authorization-admin", ProviderAccountLabel = "admin",
                Email = "admin@companyname-projectname.com", EmailVerified = true };
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            var admin = await db.Set<User>().SingleAsync(user => user.Username == "admin");
            admin.ConfirmEmail();
            admin.EnableTwoFactor(scope.ServiceProvider.GetRequiredService<TwoFactorDomainService>().ProtectSecret(secret), [], 1);
            await db.SaveChangesAsync();
        }
        const string clientId = "external-resource";
        const string clientSecret = "ExternalResource!Secret123";
        const string redirectUri = "https://resource.test/api/v1/auth/signin";
        var scopeName = new OAuthResourceOptions().Resource;
        using (var scope = host.Services.CreateScope())
        {
            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var descriptor = new OpenIddictApplicationDescriptor
            { ClientId = clientId, ClientSecret = clientSecret, ClientType = "confidential", ApplicationType = "web" };
            descriptor.RedirectUris.Add(new Uri(redirectUri));
            descriptor.Permissions.UnionWith(["ept:authorization", "ept:token", "gt:authorization_code", "rst:code", "scp:openid", "scp:" + scopeName]);
            await applications.CreateAsync(descriptor);
        }
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        using var browser = ProjectWebApplicationFactory.CreateProjectClient(host);
        browser.BaseAddress = new Uri("https://localhost");
        using var initial = await browser.GetCachedAsync(QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = clientId, ["redirect_uri"] = redirectUri, ["response_type"] = "code", ["scope"] = "openid " + scopeName,
            ["state"] = "resource-original-state", ["code_challenge"] = challenge, ["code_challenge_method"] = "S256"
        }));
        Assert.Equal(HttpStatusCode.Found, initial.StatusCode);
        var returnUrl = QueryHelpers.ParseQuery(new Uri(browser.BaseAddress, initial.Headers.Location!).Query)["returnUrl"].ToString();
        Assert.StartsWith("/connect/authorize?", returnUrl);
        var flow = await ExternalOAuthBackchannel.StartAsync(browser, returnUrl: returnUrl);
        browser.DefaultRequestHeaders.Add("Cookie", flow.Cookie);
        using var completed = await browser.PostAsJsonAsync("/api/v1/external-auth/github/complete", new { });
        Assert.True(completed.IsSuccessStatusCode, await completed.Content.ReadAsStringAsync());
        var result = await completed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(returnUrl, result.GetProperty("returnUrl").GetString());
        string cookie;
        if (twoFactor)
        {
            Assert.True(result.GetProperty("requiresTwoFactor").GetBoolean());
            var code = Totp.ComputeCode(secret, Totp.TimeStepAt(DateTimeOffset.UtcNow));
            using var second = await browser.PostAsJsonAsync("/api/v1/auth/two-factor",
                new { Token = result.GetProperty("twoFactorToken").GetString(), Code = code });
            Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());
            cookie = second.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(ProjectWebApplicationFactory.SessionCookieName + "=")).Split(';')[0];
        }
        else cookie = completed.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(ProjectWebApplicationFactory.SessionCookieName + "=")).Split(';')[0];
        browser.DefaultRequestHeaders.Remove("Cookie");
        browser.DefaultRequestHeaders.Add("Cookie", cookie);
        using var authorized = await browser.GetAsync(result.GetProperty("returnUrl").GetString());
        Assert.Equal(HttpStatusCode.Found, authorized.StatusCode);
        Assert.Equal(redirectUri, authorized.Headers.Location!.GetLeftPart(UriPartial.Path));
        var parameters = QueryHelpers.ParseQuery(authorized.Headers.Location.Query);
        Assert.Equal("resource-original-state", parameters["state"].ToString());
        Assert.False(string.IsNullOrEmpty(parameters["code"]));
        using var exchanged = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirectUri, ["code"] = parameters["code"].ToString(), ["code_verifier"] = verifier
        }));
        Assert.True(exchanged.IsSuccessStatusCode, await exchanged.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrEmpty((await exchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()));
    }
#endif

    /// <summary>候选用户名全被占用时以专用码拒绝首次外部登录：不说"用户名已存在"、不回显本服务生成的用户名，也不留下半个账号。</summary>
    [Fact]
    public async Task First_external_sign_in_is_refused_with_a_dedicated_code_when_no_username_can_be_allocated()
    {
        const string handle = "crowded";
        using var factory = new ProjectWebApplicationFactory();
        using var backchannel = new ExternalOAuthBackchannel
        {
            User = new ExternalUserInfo { ProviderId = "crowded-id", ProviderAccountLabel = handle, SuggestedUsername = handle }
        };
        using var host = backchannel.CreateHost(factory).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddTransient(provider => new UserDomainService(
                EveryUsernameTaken.Create(provider.GetRequiredService<IRepository<User, Guid>>()),
                provider.GetRequiredService<IDataFilter>(),
                provider.GetRequiredService<ICurrentTenant>(),
                provider.GetRequiredService<IPasswordHasher>(),
                provider.GetRequiredService<ILogger<UserDomainService>>()))));
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        var flow = await ExternalOAuthBackchannel.StartAsync(client);
        client.DefaultRequestHeaders.Add("Cookie", flow.Cookie);

        using var complete = await client.PostAsJsonAsync("/api/v1/external-auth/github/complete", new { });

        Assert.Equal(HttpStatusCode.Conflict, complete.StatusCode);
        var body = await complete.Content.ReadAsStringAsync();
        Assert.Equal(ExternalAuthErrorCodes.UsernameAllocationFailed, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(handle, body, StringComparison.OrdinalIgnoreCase);
        Assert.False(complete.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(value => value.StartsWith(ProjectWebApplicationFactory.SessionCookieName + "=", StringComparison.Ordinal)));

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        Assert.False(await db.Set<User>().IgnoreQueryFilters().AnyAsync(user => user.Username.StartsWith(handle)));
        Assert.False(await db.Set<ExternalLoginConnection>().IgnoreQueryFilters().AnyAsync(connection => connection.ProviderUserId == "crowded-id"));
    }

    /// <summary>用户仓储的替身：查重一律答"已占用"，其余调用转给真实仓储。</summary>
    public class EveryUsernameTaken : DispatchProxy
    {
        private IRepository<User, Guid> inner = null!;

        public static IRepository<User, Guid> Create(IRepository<User, Guid> inner)
        {
            var proxy = Create<IRepository<User, Guid>, EveryUsernameTaken>();
            ((EveryUsernameTaken)(object)proxy).inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IRepository<User, Guid>.AnyAsync)
                && args is [Expression<Func<User, bool>>, ..])
                return Task.FromResult(true);

            try
            {
                return targetMethod.Invoke(inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Throw(exception.InnerException);
                throw;
            }
        }
    }

    private static WebApplicationFactory<Program> CreateExternalAuthHost(ProjectWebApplicationFactory factory, ExternalUserInfo user) =>
        new ExternalOAuthBackchannel { User = user }.CreateHost(factory);

#if (IncludeMultiTenancy)
    private sealed record TenantIdResponse(Guid Id);
    private sealed record UserPageResponse(IReadOnlyList<UserResponse> Items);
    private sealed record UserResponse(string Email);
#endif
}
#endif
