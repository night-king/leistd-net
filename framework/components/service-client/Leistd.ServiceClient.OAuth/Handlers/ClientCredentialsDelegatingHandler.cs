using System.Net;
using System.Net.Http.Headers;
using Leistd.ServiceClient.OAuth.Options;
using Microsoft.Extensions.Options;
using OpenIddict.Client;
using static OpenIddict.Client.OpenIddictClientModels;

namespace Leistd.ServiceClient.OAuth.Handlers;

internal sealed class ClientCredentialsDelegatingHandler(string name, OpenIddictClientService client, TokenCache cache,
    IOptions<ServiceAuthenticationOptions> identity, IOptionsMonitor<ClientCredentialsOptions> options) : DelegatingHandler
{
    // 机器令牌复用时间较长，提前 60 秒为网络传输和时钟差留余量。
    private static readonly TimeSpan ExpirationBuffer = TimeSpan.FromSeconds(60);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is not null) return await base.SendAsync(request, cancellationToken);
        var value = options.Get(name);
        var key = TokenCache.Key(identity.Value.Authority, identity.Value.ClientId, name, "machine", value.Scope);
        async ValueTask<TokenCache.Token> Fetch(CancellationToken ct)
        {
            var result = await client.AuthenticateWithClientCredentialsAsync(new ClientCredentialsAuthenticationRequest
            {
                RegistrationId = DependencyInjection.RegistrationId,
                Scopes = [.. (value.Scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)], CancellationToken = ct
            });
            return new(result.AccessToken, result.AccessTokenExpirationDate);
        }
        var token = await cache.GetAsync(key, ExpirationBuffer, Fetch, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            await cache.RemoveAsync(key, cancellationToken);
        // 写操作不自动重放；由业务层决定重试，下一次请求重新获取凭据。
        return response;
    }
}
