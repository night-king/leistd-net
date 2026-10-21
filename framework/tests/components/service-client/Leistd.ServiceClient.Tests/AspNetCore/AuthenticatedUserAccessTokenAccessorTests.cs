using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Leistd.ServiceClient.Abstractions;
using Leistd.ServiceClient.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Leistd.ServiceClient.Tests.AspNetCore;

public sealed class AuthenticatedUserAccessTokenAccessorTests
{
    [Theory]
    [InlineData(false, "verified-session")]
    [InlineData(true, "missing")]
    public async Task The_first_registration_controls_whether_cookie_tickets_are_accepted(bool bearerFirst, string expected)
    {
        await using var app = await StartAsync(new FakeTimeProvider(), bearerFirst);
        using var client = app.GetTestClient();
        var response = await client.GetAsync("/issue?token=verified-session");
        client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        Assert.Equal(expected, await client.GetStringAsync("/token"));
        client.DefaultRequestHeaders.Add("Authorization", "Bearer valid");
        Assert.Equal("verified-bearer", await client.GetStringAsync("/token"));
    }

    [Fact]
    public async Task Native_cookie_and_policy_schemes_only_supply_successful_saved_tokens()
    {
        var clock = new FakeTimeProvider();
        await using var app = await StartAsync(clock);
        using var client = app.GetTestClient();
        Assert.Equal("missing", await client.GetStringAsync("/token"));
        var issue = await client.GetAsync("/issue?token=verified-session");
        client.DefaultRequestHeaders.Add("Cookie", issue.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        // 票据的首身份未认证，第二个身份已认证。
        Assert.Equal("verified-session", await client.GetStringAsync("/token"));
        client.DefaultRequestHeaders.Add("Authorization", "Bearer invalid");
        Assert.Equal("missing", await client.GetStringAsync("/token"));
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.Add("Authorization", "Bearer unauthenticated");
        Assert.Equal("missing", await client.GetStringAsync("/token"));
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.Add("Authorization", "Bearer valid");
        Assert.Equal("verified-bearer", await client.GetStringAsync("/token"));
        client.DefaultRequestHeaders.Remove("Authorization");
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("missing", await client.GetStringAsync("/token"));

        client.DefaultRequestHeaders.Remove("Cookie");
        issue = await client.GetAsync("/issue");
        client.DefaultRequestHeaders.Add("Cookie", issue.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        Assert.Equal("missing", await client.GetStringAsync("/token"));
    }

    [Fact]
    public async Task Missing_context_and_cancellation_keep_native_semantics()
    {
        using var provider = new ServiceCollection().AddAuthenticatedUserAccessTokenAccessor("Session").BuildServiceProvider();
        var accessor = provider.GetRequiredService<IUserAccessTokenAccessor>();
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        Assert.Null(await accessor.GetAccessTokenAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await accessor.GetAccessTokenAsync(new CancellationToken(canceled: true)));
        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddAuthenticatedUserAccessTokenAccessor(" "));
    }

    private static async Task<WebApplication> StartAsync(FakeTimeProvider clock, bool bearerFirst = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAuthentication()
            .AddCookie("Session", options =>
            {
                options.TimeProvider = clock;
                options.Events.OnValidatePrincipal = context =>
                {
                    context.ReplacePrincipal(new ClaimsPrincipal([new ClaimsIdentity(), .. context.Principal!.Identities]));
                    return Task.CompletedTask;
                };
            })
            .AddScheme<AuthenticationSchemeOptions, BearerHandler>("Bearer", _ => { })
            .AddPolicyScheme("Smart", null, options => options.ForwardDefaultSelector = context =>
                context.Request.Headers.ContainsKey("Authorization") ? "Bearer" : "Session");
        if (bearerFirst)
            builder.Services.AddUserAccessTokenAccessor("Bearer");
        builder.Services.AddAuthenticatedUserAccessTokenAccessor("Smart");
        builder.Services.AddUserAccessTokenAccessor("Bearer");
        var app = builder.Build();
        app.MapGet("/issue", async (HttpContext context) =>
        {
            var properties = new AuthenticationProperties { ExpiresUtc = clock.GetUtcNow().AddMinutes(1) };
            if (context.Request.Query.TryGetValue("token", out var token))
                properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = token.ToString() }]);
            await context.SignInAsync("Session", new ClaimsPrincipal(
                new ClaimsIdentity([new Claim("sub", "user")], "Session")), properties);
            return Results.StatusCode((int)HttpStatusCode.NoContent);
        });
        app.MapGet("/token", async (IUserAccessTokenAccessor accessor) =>
            Results.Text(await accessor.GetAccessTokenAsync() ?? "missing"));
        await app.StartAsync();
        return app;
    }

    private sealed class BearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var unauthenticated = Request.Headers.Authorization == "Bearer unauthenticated";
            if (Request.Headers.Authorization != "Bearer valid" && !unauthenticated)
                return Task.FromResult(AuthenticateResult.Fail("Invalid bearer"));
            var properties = new AuthenticationProperties();
            properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = "verified-bearer" }]);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal([new ClaimsIdentity(), new ClaimsIdentity([new Claim("sub", "user")],
                    unauthenticated ? null : "Bearer")]), properties, "Bearer")));
        }
    }
}
