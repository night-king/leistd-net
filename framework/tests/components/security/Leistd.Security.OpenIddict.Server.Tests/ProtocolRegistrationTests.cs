using System.Security.Claims;
using Leistd.Security.OpenIddict.Server;
using Leistd.Security.OpenIddict.Server.Pruning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using global::OpenIddict.Abstractions;
using global::OpenIddict.Server;
using Xunit;

namespace Leistd.Security.OpenIddict.Server.Tests;

public sealed class ProtocolRegistrationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task Pruning_rejects_retention_below_the_native_ten_minute_minimum(int minutes)
    {
        using var host = new HostBuilder().ConfigureServices(services => services.AddOpenIddictPruning(options => options.MinimumRetention = TimeSpan.FromMinutes(minutes), "Retention")).Build();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains("Retention:MinimumRetention", error.Message);
    }

    [Theory]
    [InlineData("urn:ietf:params:oauth:grant-type:token-exchange", 60, 120, 60)]
    [InlineData("urn:ietf:params:oauth:grant-type:token-exchange", 120, 60, 60)]
    [InlineData("client_credentials", 60, 120, 120)]
    public async Task The_native_sign_in_handler_caps_only_exchange_tokens(string grantType, int subjectSeconds, int outputSeconds, int expectedSeconds)
    {
        var services = new ServiceCollection();
        services.AddTokenExchangeExpirationLimit();
        services.AddTokenExchangeExpirationLimit();
        using var provider = services.BuildServiceProvider();
        var options = new OpenIddictServerOptions();
        foreach (var configure in provider.GetServices<IConfigureOptions<OpenIddictServerOptions>>()) configure.Configure(options);
        var descriptor = Assert.Single(options.Handlers, descriptor => descriptor.Order == OpenIddictServerHandlers.PrepareIssuedTokenPrincipal.Descriptor.Order + 1);
        var now = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        var context = new OpenIddictServerEvents.ProcessSignInContext(new OpenIddictServerTransaction())
        {
            Request = new OpenIddictRequest { GrantType = grantType },
            Principal = new ClaimsPrincipal(new ClaimsIdentity()),
            IssuedTokenPrincipal = new ClaimsPrincipal(new ClaimsIdentity())
        };
        context.Principal.SetExpirationDate(now.AddSeconds(subjectSeconds));
        context.IssuedTokenPrincipal.SetExpirationDate(now.AddSeconds(outputSeconds));
        using var scope = provider.CreateScope();
        var handler = (IOpenIddictServerHandler<OpenIddictServerEvents.ProcessSignInContext>)scope.ServiceProvider.GetRequiredService(descriptor.ServiceDescriptor.ServiceType);
        await handler.HandleAsync(context);
        Assert.Equal(now.AddSeconds(expectedSeconds), context.IssuedTokenPrincipal.GetExpirationDate());
    }
}
