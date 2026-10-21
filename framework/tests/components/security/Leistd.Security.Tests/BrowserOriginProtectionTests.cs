using System.Net;
using Leistd.Security.AspNetCore;
using Leistd.Security.AspNetCore.BrowserOrigins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Security.Tests;

public sealed class BrowserOriginProtectionTests
{
    [Theory]
    [InlineData("https://localhost", false)]
    [InlineData("https://allowed.test", false)]
    [InlineData("https://untrusted.test", true)]
    [InlineData("null", true)]
    [InlineData("https://localhost/extra", true)]
    [InlineData("https://user@localhost", true)]
    [InlineData("https://localhost?query=value", true)]
    public async Task Only_a_valid_same_or_credentialed_trusted_origin_is_allowed(string origin, bool denied)
    {
        await using var app = await StartAsync();
        using var response = await SendAsync(app, HttpMethod.Post, "/api/write", "Origin", origin);
        Assert.Equal(denied ? HttpStatusCode.Forbidden : HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("same-origin", false)]
    [InlineData("none", false)]
    [InlineData("same-site", true)]
    [InlineData("cross-site", true)]
    [InlineData("invalid", true)]
    public async Task Fetch_metadata_is_used_only_when_origin_is_absent(string site, bool denied)
    {
        await using var app = await StartAsync();
        using var response = await SendAsync(app, HttpMethod.Post, "/api/write", "Sec-Fetch-Site", site);
        Assert.Equal(denied ? HttpStatusCode.Forbidden : HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Multiple_origins_and_forged_authorization_do_not_bypass_a_rejected_origin()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://localhost/api/write");
        request.Headers.TryAddWithoutValidation("Origin", ["https://localhost", "https://allowed.test"]);
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer forged");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/write", true)]
    [InlineData("POST", "/api-other", false)]
    [InlineData("GET", "/api/read", false)]
    [InlineData("HEAD", "/api/read", false)]
    [InlineData("OPTIONS", "/api/read", false)]
    [InlineData("TRACE", "/api/read", false)]
    [InlineData("GET", "/hubs/connect", true)]
    [InlineData("POST", "/other/write", false)]
    public async Task Protected_paths_use_segment_boundaries_and_hub_paths_cover_safe_methods(string method, string path, bool denied)
    {
        await using var app = await StartAsync();
        using var response = await SendAsync(app, new HttpMethod(method), path, "Origin", "https://untrusted.test");
        Assert.Equal(denied ? HttpStatusCode.Forbidden : HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Calls_without_browser_headers_are_allowed_and_root_prefix_protects_every_path()
    {
        await using var app = await StartAsync(options => options.WritePaths = ["/"]);
        using var plain = await SendAsync(app, HttpMethod.Post, "/other");
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        using var crossSite = await SendAsync(app, HttpMethod.Post, "/other", "Origin", "https://untrusted.test");
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
    }

    [Theory]
    [InlineData("wildcard")]
    [InlineData("no-credentials")]
    public async Task A_cors_policy_that_does_not_explicitly_trust_credentials_cannot_exempt_an_origin(string mode)
    {
        await using var app = await StartAsync(options => options.CorsPolicyName = mode);
        using var response = await SendAsync(app, HttpMethod.Post, "/api/write", "Origin", "https://allowed.test");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/endpoint", "https://endpoint.test", false)]
    [InlineData("/api/endpoint", "https://allowed.test", true)]
    [InlineData("/api/disabled", "https://allowed.test", true)]
    [InlineData("/api/inline", "https://inline.test", false)]
    public async Task Endpoint_cors_metadata_takes_precedence_over_the_default(string path, string origin, bool denied)
    {
        await using var app = await StartAsync();
        using var response = await SendAsync(app, HttpMethod.Post, path, "Origin", origin);
        Assert.Equal(denied ? HttpStatusCode.Forbidden : HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Repeated_use_mounts_the_origin_check_once()
    {
        var originChecks = 0;
        await using var app = await StartAsync(cors: policy => policy.SetIsOriginAllowed(origin =>
        {
            Interlocked.Increment(ref originChecks);
            return origin == "https://allowed.test";
        }));
        using var response = await SendAsync(app, HttpMethod.Post, "/api/write", "Origin", "https://allowed.test");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, originChecks); // 原生 CORS 与来源门禁各检查一次。
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative")]
    [InlineData("/api?query=value")]
    [InlineData("/api#fragment")]
    [InlineData("/api/")]
    [InlineData("/api path")]
    public async Task Invalid_or_missing_paths_fail_at_start_with_the_actual_configuration_key(string? path)
    {
        using var host = new HostBuilder().ConfigureServices(services => services.AddBrowserOriginProtection(
            options => options.WritePaths = path is null ? [] : [path], "Origins")).Build();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains("Origins:WritePaths", error.Message);
    }

    [Fact]
    public async Task Use_without_registration_fails_when_the_pipeline_is_built()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCors();
        await using var app = builder.Build();
        var error = Assert.Throws<InvalidOperationException>(() => app.UseBrowserOriginProtection());
        Assert.Contains("AddBrowserOriginProtection", error.Message);
    }

    private static async Task<WebApplication> StartAsync(Action<BrowserOriginProtectionOptions>? configure = null,
        Action<CorsPolicyBuilder>? cors = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Origins:WritePaths:0"] = "/api" });
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.WithOrigins("https://allowed.test").AllowCredentials().AllowAnyHeader().AllowAnyMethod();
                cors?.Invoke(policy);
            });
            options.AddPolicy("endpoint", policy => policy.WithOrigins("https://endpoint.test").AllowCredentials());
            options.AddPolicy("wildcard", policy => policy.AllowAnyOrigin());
            options.AddPolicy("no-credentials", policy => policy.WithOrigins("https://allowed.test"));
        });
        builder.Services.AddBrowserOriginProtection(options =>
        {
            options.AllMethodPaths = ["/hubs"];
            configure?.Invoke(options);
        }, "Origins");
        var app = builder.Build();
        app.UseRouting();
        app.UseCors();
        Assert.Same(app, app.UseBrowserOriginProtection());
        Assert.Same(app, app.UseBrowserOriginProtection());
        app.MapPost("/api/endpoint", () => Results.Ok()).RequireCors("endpoint");
        app.MapPost("/api/disabled", () => Results.Ok()).WithMetadata(new Microsoft.AspNetCore.Cors.DisableCorsAttribute());
        app.MapPost("/api/inline", () => Results.Ok()).RequireCors(policy => policy.WithOrigins("https://inline.test").AllowCredentials());
        app.MapFallback(context => { context.Response.StatusCode = 200; return Task.CompletedTask; });
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpResponseMessage> SendAsync(WebApplication app, HttpMethod method, string path,
        string? header = null, string? value = null)
    {
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(method, "https://localhost" + path);
        if (header is not null) request.Headers.TryAddWithoutValidation(header, value);
        return await client.SendAsync(request);
    }
}
