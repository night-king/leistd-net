using System.Security.Cryptography;
using Leistd.Security.OneTimeCodes;
using Leistd.Security.OneTimeCodes.VerificationCodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Security.Tests;

public sealed class OneTimeCodeRegistrationTests
{
    [Fact]
    public async Task Configuration_binds_before_the_delegate_and_registration_preserves_host_storage()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Codes:Key"] = "short" });
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var custom = new HmacVerificationCodeDigest(Options.Create(new VerificationCodeOptions { Key = key }));
        builder.Services.AddSingleton<IVerificationCodeDigest>(custom);
        builder.Services.AddVerificationCodeDigest(configSectionPath: "Codes").AddVerificationCodeDigest(options => options.Key = key, "Codes");
        using var host = builder.Build();
        await host.StartAsync();
        Assert.Same(custom, host.Services.GetRequiredService<IVerificationCodeDigest>());
        Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(IVerificationCodeDigest));
        Assert.Equal(key, host.Services.GetRequiredService<IOptions<VerificationCodeOptions>>().Value.Key);
        await host.StopAsync();
    }

    [Fact]
    public async Task An_absent_key_allows_dependency_resolution_but_fails_when_the_default_digest_is_used()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddVerificationCodeDigest(configSectionPath: "Absent");
        using var host = builder.Build();
        await host.StartAsync();
        var digest = host.Services.GetRequiredService<IVerificationCodeDigest>();
        Assert.IsType<HmacVerificationCodeDigest>(digest);
        Assert.Throws<InvalidOperationException>(() => digest.Compute("123456"));
        await host.StopAsync();
    }

    [Theory]
    [InlineData("bad base64!")]
    [InlineData("c2hvcnQ=")]
    public async Task Invalid_nonempty_keys_fail_at_start_with_the_configured_section(string key)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Codes:Key"] = key });
        builder.Services.AddVerificationCodeDigest(configSectionPath: "Codes");
        using var host = builder.Build();
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.StartsWith("Codes:Key", Assert.Single(exception.Failures));
    }
}
