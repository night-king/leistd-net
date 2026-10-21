#if (OpenIddictServer)
using CompanyName.ProjectName.Api.Options;
using System.Globalization;

namespace CompanyName.ProjectName.UnitTests.Api;

/// <summary>令牌签发配置的校验：一条失败一项、以配置键开头，访问令牌寿命与两个证书集合的问题一次报全。</summary>
public sealed class OAuthServerOptionsValidatorTests
{
    private readonly OAuthServerOptionsValidator _validator = new();

    [Fact]
    public void Development_certificates_need_no_certificate_files()
    {
        Assert.True(_validator.Validate(null, new OAuthServerOptions { UseDevelopmentCertificates = true }).Succeeded);
    }

    [Theory]
    [InlineData(61)]
    [InlineData(90)]
    [InlineData(600)]
    public void Whole_second_lifetimes_beyond_the_refresh_lead_are_accepted(int seconds)
    {
        var options = new OAuthServerOptions { UseDevelopmentCertificates = true, AccessTokenLifetime = TimeSpan.FromSeconds(seconds) };

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    // 不长于提前刷新窗口时，刚签发的令牌就要刷新；带小数秒的值在令牌里会被截掉
    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:01:00")]
    [InlineData("00:01:00")]
    [InlineData("00:01:00.001")]
    [InlineData("00:01:30.500")]
    public void Lifetimes_within_the_refresh_lead_or_with_fractional_seconds_are_rejected(string lifetime)
    {
        foreach (var development in new[] { true, false })
        {
            var options = new OAuthServerOptions
            {
                UseDevelopmentCertificates = development,
                AccessTokenLifetime = TimeSpan.Parse(lifetime, CultureInfo.InvariantCulture),
                SigningCertificates = [new OAuthCertificate { Path = "signing.pfx" }],
                EncryptionCertificates = [new OAuthCertificate { Path = "encryption.pfx" }]
            };

            var result = _validator.Validate(null, options);

            Assert.True(result.Failed);
            Assert.StartsWith("OAuth:AccessTokenLifetime must be whole seconds", Assert.Single(result.Failures!), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Problems_in_both_collections_are_reported_together()
    {
        var options = new OAuthServerOptions
        {
            AccessTokenLifetime = TimeSpan.FromSeconds(30),
            SigningCertificates = [new OAuthCertificate { Path = "signing.pfx" }, new OAuthCertificate { Path = "" }],
            EncryptionCertificates = []
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Collection(result.Failures!,
            failure => Assert.StartsWith("OAuth:AccessTokenLifetime must be whole seconds", failure, StringComparison.Ordinal),
            failure => Assert.StartsWith("OAuth:SigningCertificates:1:Path is required", failure, StringComparison.Ordinal),
            failure => Assert.StartsWith("OAuth:EncryptionCertificates requires at least one entry", failure, StringComparison.Ordinal));
    }
}
#endif
