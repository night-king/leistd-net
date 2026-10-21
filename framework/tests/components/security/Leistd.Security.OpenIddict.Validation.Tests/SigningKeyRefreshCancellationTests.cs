using Leistd.Security.OpenIddict.Validation.SigningKeys;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;
using global::OpenIddict.Abstractions;
using global::OpenIddict.Validation;
using Xunit;

namespace Leistd.Security.OpenIddict.Validation.Tests;

public sealed class SigningKeyRefreshCancellationTests
{
    [Fact]
    public async Task The_current_request_wait_is_bounded_even_if_the_configuration_manager_does_not_cancel()
    {
        var context = Context(new PendingManager(), CancellationToken.None);
        var handler = Handler(TimeSpan.FromMilliseconds(10));
        await handler.HandleAsync(context).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(context.Principal);
        Assert.Empty(context.TokenValidationParameters.IssuerSigningKeys);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated()
    {
        using var cancellation = new CancellationTokenSource();
        var manager = new PendingManager();
        var context = Context(manager, cancellation.Token);
        var task = Handler(TimeSpan.FromSeconds(10)).HandleAsync(context).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(cancellation.Token, manager.CancellationToken);
    }

    private static RefreshSigningKeysOnUnknownKeyIdentifier Handler(TimeSpan timeout) => new(
        Options.Create(new SigningKeyRefreshOptions { FetchTimeout = timeout }), NullLogger<RefreshSigningKeysOnUnknownKeyIdentifier>.Instance);

    private static OpenIddictValidationEvents.ValidateTokenContext Context(PendingManager manager, CancellationToken cancellationToken)
    {
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(new byte[32]) { KeyId = "unknown" }, SecurityAlgorithms.HmacSha256)
        });
        return new OpenIddictValidationEvents.ValidateTokenContext(new OpenIddictValidationTransaction
        {
            Options = new OpenIddictValidationOptions { ConfigurationManager = manager }, CancellationToken = cancellationToken
        }) { Token = token, SecurityTokenHandler = new JsonWebTokenHandler(), TokenValidationParameters = new TokenValidationParameters { IssuerSigningKeys = [] } };
    }

    private sealed class PendingManager : IConfigurationManager<OpenIddictConfiguration>
    {
        public CancellationToken CancellationToken { get; private set; }
        public Task<OpenIddictConfiguration> GetConfigurationAsync(CancellationToken cancel)
        {
            CancellationToken = cancel;
            return new TaskCompletionSource<OpenIddictConfiguration>().Task;
        }
        public void RequestRefresh() { }
    }
}
