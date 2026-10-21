using Microsoft.Extensions.Options;
using global::OpenIddict.Validation.SystemNetHttp;

namespace Leistd.Security.OpenIddict.Validation.SigningKeys;

internal sealed class ConfigureSigningKeyRefreshHttpClient(IOptions<SigningKeyRefreshOptions> refresh)
    : IConfigureOptions<OpenIddictValidationSystemNetHttpOptions>
{
    public void Configure(OpenIddictValidationSystemNetHttpOptions options) =>
        options.HttpClientActions.Add(client => client.Timeout = refresh.Value.FetchTimeout);
}
