using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using global::OpenIddict.Abstractions;
using global::OpenIddict.Validation;
using static global::OpenIddict.Abstractions.OpenIddictConstants;
using static global::OpenIddict.Validation.OpenIddictValidationEvents;
using static global::OpenIddict.Validation.OpenIddictValidationHandlers;

namespace Leistd.Security.OpenIddict.Validation.SigningKeys;

/// <summary>在原生验签前为未知 kid 刷新当前请求的动态公钥。</summary>
/// <remarks>仅使用配置的签发方；保留静态键，签名、issuer、audience 和期限仍由原生处理器验证。</remarks>
public sealed class RefreshSigningKeysOnUnknownKeyIdentifier(IOptions<SigningKeyRefreshOptions> options, ILogger<RefreshSigningKeysOnUnknownKeyIdentifier> logger)
    : IOpenIddictValidationHandler<ValidateTokenContext>
{
    /// <summary>原生处理器描述；宿主可经 OpenIddict 事件 API 替换。</summary>
    public static OpenIddictValidationHandlerDescriptor Descriptor { get; }
        = OpenIddictValidationHandlerDescriptor.CreateBuilder<ValidateTokenContext>()
            .UseSingletonHandler<RefreshSigningKeysOnUnknownKeyIdentifier>()
            .SetOrder(Protection.ValidateIdentityModelToken.Descriptor.Order - 1)
            .SetType(OpenIddictValidationHandlerType.Custom)
            .Build();

    /// <inheritdoc />
    public async ValueTask HandleAsync(ValidateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Principal is not null || context.TokenValidationParameters is not { } parameters ||
            context.TokenFormat is not null and not TokenFormats.Private.JsonWebToken ||
            string.IsNullOrEmpty(context.Token) || !context.SecurityTokenHandler.CanReadToken(context.Token))
            return;

        string keyId;
        try
        {
            var token = new JsonWebToken(context.Token);
            if (token.IsEncrypted || string.IsNullOrEmpty(token.Kid)) return;
            keyId = token.Kid;
        }
        catch (ArgumentException)
        {
            return;
        }

        if (parameters.IssuerSigningKeys?.Any(key => string.Equals(key.KeyId, keyId, StringComparison.Ordinal)) == true)
            return;

        var manager = context.Options.ConfigurationManager;
        OpenIddictConfiguration configuration;
        try
        {
            manager.RequestRefresh();
            configuration = await manager.GetConfigurationAsync(context.CancellationToken)
                .WaitAsync(options.Value.FetchTimeout, context.CancellationToken);
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException or HttpRequestException)
        {
            logger.LogDebug("Refreshing the signing keys for an unknown key identifier failed ({ExceptionType}).",
                exception.GetType().Name);
            return;
        }
        var refreshed = configuration.SigningKeys;
        parameters.IssuerSigningKeys = (context.Options.TokenValidationParameters.IssuerSigningKeys ?? []).Concat(refreshed).ToArray();
        logger.LogDebug("Signing keys checked for an unknown key identifier; the key was {Result} at the issuer.",
            refreshed.Any(key => string.Equals(key.KeyId, keyId, StringComparison.Ordinal)) ? "found" : "not found");
    }
}
