#if (LocalIdentity)
using CompanyName.ProjectName.Infrastructure.Auth.Passkeys;

namespace CompanyName.ProjectName.UnitTests.Infrastructure;

/// <summary>通行密钥的依赖方：整组不配即不可用，配了就要 RP ID 与每个来源对得上，错了在启动期指出键名。</summary>
public sealed class PasskeyOptionsValidatorTests
{
    [Fact]
    public void Leaving_it_unconfigured_is_accepted_and_unavailable()
    {
        // compose 不填时注入的是空串
        var options = new PasskeyOptions { RelyingPartyId = "", Origins = [""] };

        Assert.True(Validate(options).Succeeded);
        Assert.False(options.IsConfigured);
    }

    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData("example.com", "https://app.example.com:8443")]
    [InlineData("localhost", "http://localhost:4200")]
    public void Matching_origins_are_accepted(string rpId, string origin)
    {
        Assert.True(Validate(new PasskeyOptions { RelyingPartyId = rpId, Origins = [origin] }).Succeeded);
    }

    [Theory]
    [InlineData(null, "https://example.com", "RelyingPartyId")]
    [InlineData("example.com", null, "Origins")]
    [InlineData("https://example.com", "https://example.com", "RelyingPartyId")]
    [InlineData("example.com:443", "https://example.com", "RelyingPartyId")]
    [InlineData("example.com", "http://app.example.com", "must use https")]
    [InlineData("example.com", "https://example.org", "is not under")]
    [InlineData("example.com", "https://notexample.com", "is not under")]
    [InlineData("example.com", "https://app.example.com/login", "must be an origin")]
    public void Mismatches_fail_with_the_configuration_key(string? rpId, string? origin, string expected)
    {
        var result = Validate(new PasskeyOptions { RelyingPartyId = rpId, Origins = origin is null ? [] : [origin] });

        Assert.True(result.Failed);
        Assert.Contains(PasskeyOptions.SectionName + ":", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains(expected, result.FailureMessage, StringComparison.Ordinal);
    }

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(PasskeyOptions options)
        => new PasskeyOptionsValidator().Validate(null, options);
}
#endif
