#if (RemoteTokenAuth)
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CompanyName.ProjectName.Api.HealthChecks;
using CompanyName.ProjectName.Api.HostedServices.Initializer;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

namespace CompanyName.ProjectName.UnitTests.Api;

/// <summary>验证资源服务的启动依赖确认与就绪锁存。</summary>
/// <remarks>集成测试宿主预置为已就绪，启动门禁在此独立验证。</remarks>
public class ResourceReadinessGateTests
{
    private const string Issuer = "https://identity.example.com/";
    private const string Discovery = """{"issuer":"https://identity.example.com/","jwks_uri":"https://identity.example.com/jwks"}""";
    private static readonly Uri MetadataUrl = new("https://identity.example.com/.well-known/openid-configuration");

    [Fact]
    public async Task A_two_hundred_that_is_not_a_discovery_document_is_rejected()
    {
        using var client = ClientReturning(("/.well-known/openid-configuration", "<html>login</html>"));

        await Assert.ThrowsAnyAsync<Exception>(() => ConfirmAsync(client));
    }

    [Fact]
    public async Task A_discovery_document_declaring_a_different_issuer_is_rejected()
    {
        using var client = ClientReturning(
            ("/.well-known/openid-configuration",
             """{"issuer":"https://someone-else.example.com/","jwks_uri":"https://identity.example.com/jwks"}"""));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ConfirmAsync(client));
        Assert.Contains("someone-else", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"issuer\":\"\"}")]
    public async Task A_discovery_document_without_an_issuer_is_rejected(string discovery)
    {
        using var client = ClientReturning(("/.well-known/openid-configuration", discovery));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ConfirmAsync(client));
        Assert.Contains("does not declare an issuer", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_discovery_document_without_a_jwks_uri_is_rejected()
    {
        using var client = ClientReturning(
            ("/.well-known/openid-configuration", """{"issuer":"https://identity.example.com/"}"""));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ConfirmAsync(client));
        Assert.Contains("jwks_uri", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"keys\":[]}")]
    [InlineData("{\"keys\":[{\"kty\":\"RSA\",\"kid\":\"k1\"}]}")]
    [InlineData("{\"keys\":[{\"kty\":\"EC\",\"kid\":\"k1\"}]}")]
    [InlineData("{\"keys\":[{\"kty\":\"unknown\",\"kid\":\"k1\"}]}")]
    public async Task A_key_set_without_usable_signing_keys_is_rejected(string jwks)
    {
        using var client = ClientReturning(
            ("/.well-known/openid-configuration", Discovery), ("/jwks", jwks));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ConfirmAsync(client));
        Assert.Contains("no signing keys", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_set_containing_only_encryption_keys_is_rejected()
    {
        using var client = ClientReturning(
            ("/.well-known/openid-configuration", Discovery), ("/jwks", CreateRsaKeySet(use: "enc")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ConfirmAsync(client));
    }

    [Fact]
    public async Task A_two_hundred_that_is_not_a_key_set_is_rejected()
    {
        using var client = ClientReturning(
            ("/.well-known/openid-configuration", Discovery), ("/jwks", "<html>error</html>"));

        await Assert.ThrowsAnyAsync<Exception>(() => ConfirmAsync(client));
    }

    [Fact]
    public async Task A_valid_discovery_document_and_key_set_confirms()
    {
        using var client = ClientReturning(
            ("/.well-known/openid-configuration", Discovery), ("/jwks", CreateRsaKeySet()));

        await ConfirmAsync(client);
    }

    [Fact]
    public async Task An_RSA_key_with_a_certificate_chain_confirms()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Readiness", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2036, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var parameters = rsa.ExportParameters(false);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA", kid = "certificate",
                    n = Base64UrlEncoder.Encode(parameters.Modulus!), e = Base64UrlEncoder.Encode(parameters.Exponent!),
                    x5c = new[] { Convert.ToBase64String(certificate.RawData) }
                }
            }
        });
        using var client = ClientReturning(("/.well-known/openid-configuration", Discovery), ("/jwks", jwks));

        await ConfirmAsync(client);
    }

    [Theory]
    [InlineData("https://identity.example.com")]
    [InlineData("https://Identity.Example.com/")]
    [InlineData("https://identity.example.com//")]
    public async Task An_issuer_differing_only_in_case_or_trailing_slash_is_rejected(string declared)
    {
        // issuer 归一化由配置端完成，门禁按原始值精确比较。
        using var client = ClientReturning(
            ("/.well-known/openid-configuration", $$"""{"issuer":"{{declared}}","jwks_uri":"https://identity.example.com/jwks"}"""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ConfirmAsync(client));
    }

    [Fact]
    public async Task An_HTTP_failure_is_rejected()
    {
        using var client = ClientReturning();

        await Assert.ThrowsAsync<HttpRequestException>(() => ConfirmAsync(client));
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        using var client = ClientReturning(("/.well-known/openid-configuration", Discovery));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RemoteIdentityReadinessInitializer.ConfirmAsync(client, Issuer, MetadataUrl, cancel.Token));
    }

    [Fact]
    public async Task An_HTTP_timeout_is_propagated()
    {
        using var client = new HttpClient(new BlockingHandler()) { Timeout = TimeSpan.FromMilliseconds(100) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConfirmAsync(client));
    }

    [Fact]
    public async Task Readiness_is_unhealthy_until_remote_identity_is_confirmed()
    {
        var check = new RemoteIdentityReadinessHealthCheck();

        var before = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, before.Status);

        check.MarkReady();

        var after = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, after.Status);
    }

    /// <remarks>确认后锁存，避免签发方短暂故障摘掉仍可使用缓存的实例。</remarks>
    [Fact]
    public void Readiness_never_falls_back_once_confirmed()
    {
        var gate = new RemoteIdentityReadinessHealthCheck();
        gate.MarkReady();

        Assert.True(gate.IsReady);
        Assert.Null(typeof(RemoteIdentityReadinessHealthCheck).GetMethod("MarkNotReady"));
        Assert.DoesNotContain(
            typeof(RemoteIdentityReadinessHealthCheck).GetProperties(),
            property => property.Name == nameof(RemoteIdentityReadinessHealthCheck.IsReady) && property.CanWrite);
    }

    private static Task ConfirmAsync(HttpClient client) =>
        RemoteIdentityReadinessInitializer.ConfirmAsync(client, Issuer, MetadataUrl, CancellationToken.None);

    private static string CreateRsaKeySet(string use = "sig")
    {
        using var rsa = RSA.Create(2048);
        var parameters = rsa.ExportParameters(false);
        var key = new
        {
            kty = "RSA", kid = "valid", use,
            n = Base64UrlEncoder.Encode(parameters.Modulus!), e = Base64UrlEncoder.Encode(parameters.Exponent!)
        };
        return JsonSerializer.Serialize(new { keys = new[] { key } });
    }

    private static HttpClient ClientReturning(params (string PathSuffix, string Body)[] responses) =>
        new(new StubHandler(responses));

    private sealed class StubHandler(IReadOnlyList<(string PathSuffix, string Body)> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            var match = responses.FirstOrDefault(response => path.EndsWith(response.PathSuffix, StringComparison.Ordinal));
            return Task.FromResult(match.Body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(match.Body) });
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocked request must be cancelled.");
        }
    }
}
#endif
