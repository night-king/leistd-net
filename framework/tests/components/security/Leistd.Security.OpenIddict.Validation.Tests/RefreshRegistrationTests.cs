using Leistd.Security.OpenIddict.Validation;
using Leistd.Security.OpenIddict.Validation.SigningKeys;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using global::OpenIddict.Validation;
using global::OpenIddict.Validation.SystemNetHttp;
using Xunit;

namespace Leistd.Security.OpenIddict.Validation.Tests;

public sealed class RefreshRegistrationTests
{
    [Fact]
    public async Task Configuration_and_delegate_control_the_actual_native_http_timeout()
    {
        var time = new FakeTimeProvider();
        using var host = new HostBuilder().ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Keys:FetchTimeout"] = "00:00:03", ["Keys:MinimumInterval"] = "00:00:09"
        })).ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(time);
            services.AddOpenIddict().AddValidation(options =>
            {
                options.SetIssuer("https://identity.test/");
                options.UseSystemNetHttp();
            });
            services.AddSigningKeyRefresh(options => options.FetchTimeout = TimeSpan.FromSeconds(2), "Keys");
            services.AddSigningKeyRefresh(configSectionPath: "Keys");
        }).Build();
        await host.StartAsync();
        Assert.Same(time, host.Services.GetRequiredService<TimeProvider>());
        var settings = host.Services.GetRequiredService<IOptions<SigningKeyRefreshOptions>>().Value;
        Assert.Equal(TimeSpan.FromSeconds(3), settings.FetchTimeout);
        Assert.Equal(TimeSpan.FromSeconds(9), settings.MinimumInterval);
        var validation = host.Services.GetRequiredService<IOptions<OpenIddictValidationOptions>>().Value;
        Assert.IsType<SigningKeyRefreshThrottle>(validation.ConfigurationManager);
        Assert.Single(validation.Handlers, descriptor => descriptor == RefreshSigningKeysOnUnknownKeyIdentifier.Descriptor);
        Assert.NotNull(host.Services.GetRequiredService<RefreshSigningKeysOnUnknownKeyIdentifier>());
        using var client = new HttpClient();
        foreach (var action in host.Services.GetRequiredService<IOptions<OpenIddictValidationSystemNetHttpOptions>>().Value.HttpClientActions) action(client);
        Assert.Equal(settings.FetchTimeout, client.Timeout);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("Keys:FetchTimeout", "00:00:00")]
    [InlineData("Keys:FetchTimeout", "30.00:00:00")]
    [InlineData("Keys:MinimumInterval", "00:00:00")]
    public async Task Invalid_refresh_settings_fail_at_start_with_the_actual_configuration_path(string key, string value)
    {
        using var host = new HostBuilder().ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }))
            .ConfigureServices(services => services.AddSigningKeyRefresh(configSectionPath: "Keys")).Build();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains(key, error.Message);
    }

}
