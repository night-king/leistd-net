using System.Text;
using Leistd.Security.OneTimeCodes;
using Xunit;

namespace Leistd.Security.Tests;

/// <summary>按 RFC 6238 附录 B 验证 6 位结果。</summary>
public class TotpTests
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Matches_the_rfc_6238_test_vectors(long unixSeconds, string expected)
    {
        var at = DateTimeOffset.UnixEpoch.AddSeconds(unixSeconds);

        Assert.Equal(expected, Totp.ComputeCode(RfcSecret, Totp.TimeStepAt(at)));
    }

    [Fact]
    public void Accepts_one_step_of_drift_either_way_and_rejects_further()
    {
        var now = DateTimeOffset.UnixEpoch.AddSeconds(1234567890);
        var step = Totp.TimeStepAt(now);

        Assert.Equal(step - 1, Totp.Verify(RfcSecret, Totp.ComputeCode(RfcSecret, step - 1), now, null));
        Assert.Equal(step + 1, Totp.Verify(RfcSecret, Totp.ComputeCode(RfcSecret, step + 1), now, null));
        Assert.Null(Totp.Verify(RfcSecret, Totp.ComputeCode(RfcSecret, step - 2), now, null));
    }

    [Fact]
    public void A_step_already_used_is_not_accepted_again()
    {
        var now = DateTimeOffset.UnixEpoch.AddSeconds(1234567890);
        var step = Totp.TimeStepAt(now);

        Assert.Null(Totp.Verify(RfcSecret, Totp.ComputeCode(RfcSecret, step), now, lastUsedStep: step));
    }

    [Fact]
    public void Base32_round_trips_and_ignores_case_and_spacing()
    {
        var secret = Totp.GenerateSecret();
        var encoded = Totp.FormatSecret(secret);

        Assert.Equal(secret, Totp.ParseSecret(encoded));
        Assert.Equal(secret, Totp.ParseSecret(string.Join(' ', encoded.ToLowerInvariant().Chunk(4).Select(c => new string(c)))));
        Assert.Null(Totp.ParseSecret("not*base32"));
    }

    [Fact]
    public void Recovery_codes_match_regardless_of_case_and_separators()
    {
        var code = RecoveryCodes.Generate()[0];

        Assert.Equal(RecoveryCodes.Hash(code), RecoveryCodes.Hash(code.ToUpperInvariant().Replace("-", " ")));
    }
    [Theory]
    [InlineData("f", "MY======")]
    [InlineData("fo", "MZXQ====")]
    [InlineData("foo", "MZXW6===")]
    [InlineData("foob", "MZXW6YQ=")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI======")]
    public void Secret_import_matches_the_rfc_4648_vectors(string plain, string encoded)
    {
        var bytes = Encoding.ASCII.GetBytes(plain);
        Assert.Equal(encoded.TrimEnd('='), Totp.FormatSecret(bytes));
        Assert.Equal(bytes, Totp.ParseSecret(encoded));
    }

    [Theory]
    [InlineData("M")]
    [InlineData("MZ")]
    [InlineData("MY=")]
    [InlineData("M=Y======")]
    [InlineData("")]
    public void Secret_import_rejects_incomplete_or_noncanonical_encodings(string encoded)
    {
        Assert.Null(Totp.ParseSecret(encoded));
    }

    [Fact]
    public void Time_offsets_do_not_change_steps_and_verification_handles_the_unix_epoch()
    {
        var instant = DateTimeOffset.UnixEpoch.AddSeconds(59);
        Assert.Equal(Totp.TimeStepAt(instant), Totp.TimeStepAt(instant.ToOffset(TimeSpan.FromHours(8))));
        Assert.Equal(0, Totp.Verify(RfcSecret, Totp.ComputeCode(RfcSecret, 0), DateTimeOffset.UnixEpoch, null));
        Assert.Null(Totp.Verify(RfcSecret, "１２３４５６", instant, null));
    }

    [Fact]
    public void Recovery_codes_have_the_documented_entropy_format_and_uri_labels_are_escaped()
    {
        var codes = RecoveryCodes.Generate();
        Assert.Equal(10, codes.Count);
        Assert.Equal(10, codes.Distinct().Count());
        Assert.All(codes, code => Assert.Matches("^[a-z2-7]{4}(-[a-z2-7]{4}){3}$", code));
        var uri = Totp.BuildUri("Example & Co", "user+label@example.com", RfcSecret);
        Assert.Contains("Example%20%26%20Co:user%2Blabel%40example.com", uri);
        Assert.Contains("secret=" + Totp.FormatSecret(RfcSecret), uri);
        Assert.Contains("algorithm=SHA1&digits=6&period=30", uri);
    }
}
