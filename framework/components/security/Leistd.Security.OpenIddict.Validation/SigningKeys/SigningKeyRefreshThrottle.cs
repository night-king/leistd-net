using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using global::OpenIddict.Abstractions;

namespace Leistd.Security.OpenIddict.Validation.SigningKeys;

/// <summary>按每副本间隔限制请求刷新，自动刷新仍交给原生管理器。</summary>
public sealed class SigningKeyRefreshThrottle(
    IConfigurationManager<OpenIddictConfiguration> inner, TimeProvider time, IOptions<SigningKeyRefreshOptions> options, ILogger<SigningKeyRefreshThrottle> logger)
    : IConfigurationManager<OpenIddictConfiguration>
{
    private readonly Lock _gate = new();
    private DateTimeOffset _nextRefresh = DateTimeOffset.MinValue;

    /// <inheritdoc />
    public Task<OpenIddictConfiguration> GetConfigurationAsync(CancellationToken cancel) => inner.GetConfigurationAsync(cancel);

    /// <inheritdoc />
    public void RequestRefresh()
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (now < _nextRefresh) return;
            _nextRefresh = now + options.Value.MinimumInterval;
            inner.RequestRefresh();
        }
        logger.LogInformation("Requested a signing key refresh from the configured issuer.");
    }
}
