using System.Net;
using System.Net.Http.Headers;
using Leistd.ServiceClient.Abstractions;
using Leistd.ServiceClient.Exceptions;
using Leistd.ServiceClient.OAuth.Options;
using Microsoft.Extensions.Options;
using OpenIddict.Client;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Client.OpenIddictClientModels;

namespace Leistd.ServiceClient.OAuth.Handlers;

internal sealed class TokenExchangeDelegatingHandler(string name, OpenIddictClientService client, TokenCache cache,
    IOptions<ServiceAuthenticationOptions> identity, IOptionsMonitor<TokenExchangeOptions> options,
    IUserAccessTokenAccessor accessor) : DelegatingHandler
{
    // 用户交换令牌寿命较短，提前 10 秒留余量，避免沿用机器缓冲而过早放弃缓存。
    private static readonly TimeSpan ExpirationBuffer = TimeSpan.FromSeconds(10);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is not null)
            throw new ServiceClientException("Token Exchange does not allow a preset Authorization header.");
        var subject = await accessor.GetAccessTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(subject))
            throw new ServiceClientException("Token Exchange requires an authenticated user access token.");
        var target = options.Get(name);
        var key = TokenCache.Key(identity.Value.Authority, identity.Value.ClientId, name, target.Audience, target.Scope, subject);
        async ValueTask<TokenCache.Token> Fetch(CancellationToken ct)
        {
            var result = await client.AuthenticateWithTokenExchangeAsync(new TokenExchangeAuthenticationRequest
            {
                RegistrationId = DependencyInjection.RegistrationId, SubjectToken = subject,
                SubjectTokenType = TokenTypeIdentifiers.AccessToken, RequestedTokenType = TokenTypeIdentifiers.AccessToken,
                Audiences = [target.Audience], Scopes = [target.Scope], DisableUserInfo = true, CancellationToken = ct
            });
            if (result.IssuedTokenType != TokenTypeIdentifiers.AccessToken)
                throw new ServiceClientException("The issuer returned an unsupported exchanged token type.");
            return new(result.IssuedToken, result.IssuedTokenExpirationDate);
        }
        var token = await cache.GetAsync(key, ExpirationBuffer, Fetch, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await cache.RemoveAsync(key, cancellationToken);
        return response;
    }
}
