using CompanyName.ProjectName.Domain.Shared.Security.PasswordHash;
using CompanyName.ProjectName.Infrastructure;
using CompanyName.ProjectName.Infrastructure.Shared.Security.PasswordHash;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CompanyName.ProjectName.UnitTests.Infrastructure;

public sealed class PasswordHashingTests
{
    private const string Password = "PasswordHashingTests!Pw";

    [Fact]
    public void Password_hashes_interoperate_with_native_identity_in_both_directions()
    {
        var native = NativeHasher(1000);
        var adapter = new IdentityPasswordHasher(native);

        Assert.Equal(PasswordVerificationResult.Success,
            native.VerifyHashedPassword(new object(), adapter.HashPassword(Password), Password));
        Assert.Equal(PasswordVerificationStatus.Succeeded,
            adapter.VerifyPassword(native.HashPassword(new object(), Password), Password));
        Assert.Equal(PasswordVerificationStatus.Failed,
            adapter.VerifyPassword(adapter.HashPassword(Password), "wrong-password"));
    }

    [Fact]
    public void The_same_password_hashes_differently_every_time()
    {
        var adapter = new IdentityPasswordHasher(NativeHasher(1000));

        Assert.NotEqual(adapter.HashPassword(Password), adapter.HashPassword(Password));
    }

    [Fact]
    public void A_lower_cost_hash_verifies_and_requests_rehashing()
    {
        var older = NativeHasher(100);
        var adapter = new IdentityPasswordHasher(NativeHasher(1000));
        var oldHash = older.HashPassword(new object(), Password);

        Assert.Equal(PasswordVerificationStatus.RehashNeeded, adapter.VerifyPassword(oldHash, Password));
        Assert.Equal(PasswordVerificationStatus.Failed, adapter.VerifyPassword(oldHash, "wrong-password"));
        Assert.Equal(PasswordVerificationStatus.Succeeded,
            adapter.VerifyPassword(adapter.HashPassword(Password), Password));
    }

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("cGxhaW4=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AQAJJ8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void An_invalid_or_custom_format_hash_returns_failed(string hash)
    {
        var adapter = new IdentityPasswordHasher(NativeHasher(1000));

        Assert.Equal(PasswordVerificationStatus.Failed, adapter.VerifyPassword(hash, Password));
    }

    [Theory]
    [InlineData(null, 220000)]
    [InlineData("1000", 1000)]
    public void Infrastructure_applies_the_default_cost_then_configuration(string? configured, int expected)
    {
        var settings = new Dictionary<string, string?>();
        if (configured is not null) settings["PasswordHash:IterationCount"] = configured;
        using var provider = Register(settings).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<PasswordHasherOptions>>().Value;
        Assert.Equal(expected, options.IterationCount);
        Assert.Equal(PasswordHasherCompatibilityMode.IdentityV3, options.CompatibilityMode);
        var adapter = provider.GetRequiredService<IPasswordHasher>();
        Assert.Equal(PasswordVerificationStatus.Succeeded,
            adapter.VerifyPassword(adapter.HashPassword(Password), Password));
    }

    [Theory]
    [InlineData("PasswordHash:IterationCount", "0")]
    [InlineData("PasswordHash:IterationCount", "-1")]
    [InlineData("PasswordHash:CompatibilityMode", "IdentityV2")]
    public void Infrastructure_rejects_invalid_native_password_options(string key, string value)
    {
        using var provider = Register(new Dictionary<string, string?> { [key] = value }).BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<PasswordHasherOptions>>().Value);
    }

    [Fact]
    public void Host_password_services_survive_repeated_registration()
    {
        var native = NativeHasher(1000);
        var adapter = new IdentityPasswordHasher(native);
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IPasswordHasher<object>>(native);
        services.AddSingleton<IPasswordHasher>(adapter);
        var configuration = new ConfigurationBuilder().Build();
        services.AddInfrastructureServices(configuration).AddInfrastructureServices(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Same(native, provider.GetRequiredService<IPasswordHasher<object>>());
        Assert.Same(adapter, provider.GetRequiredService<IPasswordHasher>());
    }

    private static PasswordHasher<object> NativeHasher(int iterations) =>
        new(Options.Create(new PasswordHasherOptions { IterationCount = iterations }));

    private static IServiceCollection Register(Dictionary<string, string?> settings) =>
        new ServiceCollection().AddLogging().AddInfrastructureServices(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
}
