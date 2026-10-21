using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using global::OpenIddict.Validation;

namespace Leistd.Security.OpenIddict.Validation.SigningKeys;

internal sealed class ThrottleSigningKeyRefresh(TimeProvider time, IOptions<SigningKeyRefreshOptions> refresh, ILogger<SigningKeyRefreshThrottle> logger)
    : IPostConfigureOptions<OpenIddictValidationOptions>
{
    public void PostConfigure(string? name, OpenIddictValidationOptions options)
    {
        if (options.ConfigurationManager is not null and not SigningKeyRefreshThrottle)
            options.ConfigurationManager = new SigningKeyRefreshThrottle(options.ConfigurationManager, time, refresh, logger);
    }
}
