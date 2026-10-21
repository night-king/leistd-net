using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Leistd.Security.OpenIddict.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using global::OpenIddict.Validation.AspNetCore;
using Xunit;

namespace Leistd.Security.OpenIddict.Validation.Tests;

public sealed class SigningKeyRotationTests
{
    [Fact]
    public async Task A_new_key_is_accepted_in_the_same_request_and_forged_signatures_still_fail()
    {
        using var issuer = new Issuer();
        await using var server = await ServerAsync(issuer);
        using var client = server.GetTestClient();
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, issuer.Token(issuer.Current)));
        issuer.Rotate();
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, issuer.Token(issuer.Current)));
        Assert.Equal(2, issuer.KeyRequests);
        using var forgedRsa = RSA.Create(2048);
        var forged = new RsaSecurityKey(forgedRsa) { KeyId = "forged" };
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, issuer.Token(forged)));
        Assert.Equal(2, issuer.KeyRequests);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_valid_cached_keys_without_accepting_the_unknown_key()
    {
        using var issuer = new Issuer();
        await using var server = await ServerAsync(issuer);
        using var client = server.GetTestClient();
        var original = issuer.Token(issuer.Current);
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, original));
        issuer.Rotate();
        issuer.Unavailable = true;
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, issuer.Token(issuer.Current)));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, original));
    }

    private static async Task<WebApplication> ServerAsync(Issuer issuer)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        var services = builder.Services;
        services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new Transport(issuer));
        services.AddOpenIddict().AddValidation(options =>
        {
            options.SetIssuer(Issuer.Address);
            options.AddAudiences("api");
            options.UseSystemNetHttp();
            options.UseAspNetCore();
        });
        services.AddSigningKeyRefresh();
        var app = builder.Build();
        app.UseAuthentication();
        app.Run(context =>
        {
            context.Response.StatusCode = context.User.Identity?.IsAuthenticated == true ? 200 : 401;
            return Task.CompletedTask;
        });
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.test/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private sealed class Transport(Issuer issuer) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            if (builder.Name?.StartsWith("OpenIddict.Validation.SystemNetHttp", StringComparison.Ordinal) == true)
                builder.PrimaryHandler = new ForwardingHandler(issuer);
        };
    }

    private sealed class ForwardingHandler(Issuer issuer) : HttpClientHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(issuer.Respond(request));
    }

    private sealed class Issuer : IDisposable
    {
        public const string Address = "https://identity.test/";
        public RsaSecurityKey Current { get; private set; } = NewKey();
        private RsaSecurityKey? _previous;
        public int KeyRequests { get; private set; }
        public bool Unavailable { get; set; }
        private static RsaSecurityKey NewKey() => new(RSA.Create(2048)) { KeyId = Guid.NewGuid().ToString() };
        public void Rotate() { _previous = Current; Current = NewKey(); }
        public string Token(RsaSecurityKey key) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Address, Audience = "api", TokenType = "at+jwt", Claims = new Dictionary<string, object> { ["sub"] = "user" },
            IssuedAt = DateTime.UtcNow, NotBefore = DateTime.UtcNow.AddSeconds(-5), Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        });
        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            if (request.RequestUri!.AbsolutePath == "/.well-known/openid-configuration")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { issuer = Address, jwks_uri = Address + "jwks" }) };
            KeyRequests++;
            if (Unavailable) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var keys = new[] { Current, _previous }.OfType<RsaSecurityKey>().Select(key =>
            {
                var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(key);
                return new { kty = jwk.Kty, kid = key.KeyId, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E };
            }).ToArray();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { keys }) };
        }
        public void Dispose() { Current.Rsa.Dispose(); _previous?.Rsa.Dispose(); }
    }
}
