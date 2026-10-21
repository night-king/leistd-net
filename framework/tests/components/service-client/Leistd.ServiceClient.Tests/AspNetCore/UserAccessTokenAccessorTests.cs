using System.Security.Claims;
using Leistd.ServiceClient.Abstractions;
using Leistd.ServiceClient.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.ServiceClient.Tests.AspNetCore;

public sealed class UserAccessTokenAccessorTests
{
    private sealed class Authentication : IAuthenticationService
    {
        internal bool AcceptBearer { get; set; }
        internal string? VerifiedToken { get; set; }
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            if (scheme != "Bearer" || !AcceptBearer) return Task.FromResult(AuthenticateResult.NoResult());
            var properties = new AuthenticationProperties();
            properties.StoreTokens([new AuthenticationToken
            {
                Name = "access_token", Value = VerifiedToken ?? context.Request.Headers.Authorization.ToString()[7..]
            }]);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user")], "Bearer")), properties, "Bearer")));
        }
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    [Fact]
    public async Task Cookie_identity_cannot_validate_an_arbitrary_bearer_header()
    {
        var authentication = new Authentication();
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(authentication);
        services.AddUserAccessTokenAccessor("Bearer");
        using var provider = services.BuildServiceProvider();
        var contexts = provider.GetRequiredService<IHttpContextAccessor>();
        var accessor = provider.GetRequiredService<IUserAccessTokenAccessor>();
        Assert.Null(await accessor.GetAccessTokenAsync());
        var first = new DefaultHttpContext { RequestServices = provider, User = new ClaimsPrincipal(new ClaimsIdentity("Cookie")) };
        first.Request.Headers.Authorization = "Bearer unverified"; contexts.HttpContext = first;
        Assert.Null(await accessor.GetAccessTokenAsync());
        authentication.AcceptBearer = true;
        Assert.Equal("unverified", await accessor.GetAccessTokenAsync());
        var second = new DefaultHttpContext { RequestServices = provider };
        second.Request.Headers.Authorization = "Bearer second-request"; contexts.HttpContext = second;
        Assert.Equal("second-request", await accessor.GetAccessTokenAsync());
        authentication.VerifiedToken = "authenticated-token";
        second.Request.Headers.Authorization = "Bearer changed-after-authentication";
        Assert.Equal("authenticated-token", await accessor.GetAccessTokenAsync());
        contexts.HttpContext = null;
        Assert.Null(await accessor.GetAccessTokenAsync());
    }
}
