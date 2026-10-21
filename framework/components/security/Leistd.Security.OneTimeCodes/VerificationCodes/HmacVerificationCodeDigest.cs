using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Leistd.Security.OneTimeCodes.VerificationCodes;

/// <summary>使用稳定服务端密钥计算 HMAC-SHA256，按固定时间比较摘要。</summary>
public sealed class HmacVerificationCodeDigest(IOptions<VerificationCodeOptions> options)
    : IVerificationCodeDigest
{
    private byte[] Key =>
        options.Value.TryGetKeyBytes(out var key)
            ? key
            : throw new InvalidOperationException(
                $"VerificationCodeOptions.Key is not configured or is too short; " +
                $"provide at least {VerificationCodeOptions.MinimumKeyBytes} base64-encoded bytes.");

    /// <inheritdoc />
    public string Compute(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return Convert.ToBase64String(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(code)));
    }

    /// <inheritdoc />
    public bool Matches(string digest, string code)
    {
        if (string.IsNullOrEmpty(digest) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(digest),
                Convert.FromBase64String(Compute(code)));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
