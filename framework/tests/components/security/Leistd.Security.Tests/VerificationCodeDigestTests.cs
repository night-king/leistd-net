using Leistd.Security.OneTimeCodes.VerificationCodes;
using Xunit;
using Microsoft.Extensions.Options;

namespace Leistd.Security.Tests;

public class VerificationCodeDigestTests
{
    private static readonly string Key = Convert.ToBase64String(
        Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    private static HmacVerificationCodeDigest Create() =>
        new(Options.Create(new VerificationCodeOptions { Key = Key }));

    [Fact]
    public void The_same_code_yields_the_same_digest_across_instances()
    {
        Assert.Equal(Create().Compute("123456"), Create().Compute("123456"));
    }

    [Fact]
    public void A_digest_matches_only_its_own_code()
    {
        var digest = Create().Compute("123456");

        Assert.True(Create().Matches(digest, "123456"));
        Assert.False(Create().Matches(digest, "123457"));
    }

    [Fact]
    public void A_malformed_digest_does_not_match_and_does_not_throw()
    {
        Assert.False(Create().Matches("not-base64!!", "123456"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("c2hvcnQ=")]
    public void A_missing_or_short_key_fails_on_use_not_on_construction(string? key)
    {
        var digest = new HmacVerificationCodeDigest(
            Options.Create(new VerificationCodeOptions { Key = key }));

        Assert.Throws<InvalidOperationException>(() => digest.Compute("123456"));
    }
    [Fact]
    public void Different_keys_cannot_validate_each_others_digest()
    {
        var other = new HmacVerificationCodeDigest(Options.Create(new VerificationCodeOptions
        {
            Key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
        }));
        Assert.False(other.Matches(Create().Compute("123456"), "123456"));
    }
}
