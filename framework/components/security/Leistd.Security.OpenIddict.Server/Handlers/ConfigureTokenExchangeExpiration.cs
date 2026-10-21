using Microsoft.Extensions.Options;
using global::OpenIddict.Server;

namespace Leistd.Security.OpenIddict.Server.Handlers;

internal sealed class ConfigureTokenExchangeExpiration : IConfigureOptions<OpenIddictServerOptions>
{
    internal static readonly OpenIddictServerHandlerDescriptor Descriptor =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ProcessSignInContext>()
            .UseScopedHandler<TokenExchangeExpirationHandler>()
            .SetOrder(OpenIddictServerHandlers.PrepareIssuedTokenPrincipal.Descriptor.Order + 1)
            .Build();

    public void Configure(OpenIddictServerOptions options)
    {
        if (!options.Handlers.Contains(Descriptor)) options.Handlers.Add(Descriptor);
    }
}
