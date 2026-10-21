using Microsoft.Extensions.Options;
using global::OpenIddict.Validation;

namespace Leistd.Security.OpenIddict.Validation.SigningKeys;

internal sealed class ConfigureSigningKeyRefresh : IConfigureOptions<OpenIddictValidationOptions>
{
    public void Configure(OpenIddictValidationOptions options)
    {
        if (!options.Handlers.Contains(RefreshSigningKeysOnUnknownKeyIdentifier.Descriptor))
            options.Handlers.Add(RefreshSigningKeysOnUnknownKeyIdentifier.Descriptor);
    }
}
