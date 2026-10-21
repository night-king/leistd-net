#if (RemoteTokenAuth)
using System.Text.Json;
using CompanyName.ProjectName.Api.HealthChecks;
using CompanyName.ProjectName.Api.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CompanyName.ProjectName.Api.HostedServices.Initializer;

/// <summary>启动时确认远端签发方及可用签名密钥，成功后锁存就绪状态。</summary>
/// <remarks>HTTP 成功不足以就绪；需要业务凭据的租户路由不属于本探针的确认范围。</remarks>
internal sealed class RemoteIdentityReadinessInitializer(
    RemoteIdentityReadinessHealthCheck readiness,
    IHttpClientFactory httpClientFactory,
    IOptions<RemoteIdentityOptions> remoteIdentityOptions,
    ILogger<RemoteIdentityReadinessInitializer> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    /// <summary>确认 issuer 精确匹配，且至少有一把可解析的签名密钥。</summary>
    /// <remarks>
    /// JWKS 的完整协议校验由令牌验证器执行；包含缺损密钥的集合可能被整体拒绝。
    /// </remarks>
    internal static async Task ConfirmAsync(
        HttpClient client,
        string issuer,
        Uri metadataUrl,
        CancellationToken cancellationToken)
    {
        using var metadataResponse = await client.GetAsync(metadataUrl, cancellationToken);
        metadataResponse.EnsureSuccessStatusCode();

        // 必须解析正文，才能识别返回 200 的 HTML 错误页或登录页。
        using var document = JsonDocument.Parse(
            await metadataResponse.Content.ReadAsByteArrayAsync(cancellationToken));

        var declaredIssuer = document.RootElement.TryGetProperty("issuer", out var issuerElement)
            ? issuerElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(declaredIssuer))
        {
            throw new InvalidOperationException(
                $"The discovery document at {metadataUrl} does not declare an issuer.");
        }

        // issuer 是安全标识符；门禁必须与令牌校验器采用相同的精确比较。
        if (!string.Equals(declaredIssuer, issuer, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The discovery document at {metadataUrl} declares issuer '{declaredIssuer}', " +
                $"but this service is configured for '{issuer}'.");
        }

        var jwksUri = document.RootElement.TryGetProperty("jwks_uri", out var jwksElement)
            ? jwksElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(jwksUri))
        {
            throw new InvalidOperationException(
                $"The discovery document at {metadataUrl} does not declare a jwks_uri.");
        }

        using var jwksResponse = await client.GetAsync(jwksUri, cancellationToken);
        jwksResponse.EnsureSuccessStatusCode();

        var jwks = new JsonWebKeySet(
            await jwksResponse.Content.ReadAsStringAsync(cancellationToken));
        if (jwks.GetSigningKeys().Count == 0)
        {
            throw new InvalidOperationException(
                $"The JWKS at {jwksUri} contains no signing keys; token validation cannot succeed.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 使用已校验选项，避免重读原始配置破坏“保持未就绪并重试”的失败语义。
        var options = remoteIdentityOptions.Value;
        var issuer = options.Issuer!;
        var metadataUrl = options.MetadataUrl;
        var client = httpClientFactory.CreateClient(nameof(RemoteIdentityReadinessInitializer));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConfirmAsync(client, issuer, metadataUrl, stoppingToken);

                readiness.MarkReady();
                logger.LogInformation(
                    "Remote identity metadata and signing keys confirmed at {MetadataUrl}; " +
                    "the service is now ready.",
                    metadataUrl);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // 保持未就绪并重试，让编排系统停止流量而非触发崩溃循环。
                logger.LogWarning(
                    exception,
                    "Remote identity metadata at {MetadataUrl} is not usable yet; retrying in {Delay}.",
                    metadataUrl,
                    RetryDelay);

                try
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
#endif
