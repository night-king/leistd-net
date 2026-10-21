using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Leistd.Security.OneTimeCodes;

/// <summary>RFC 6238 TOTP：HMAC-SHA1、30 秒步长、6 位码，容忍前后各一步。</summary>
/// <remarks>调用方保护密钥并原子保存成功验证返回的步序号；组件不存储消费状态。</remarks>
public static class Totp
{
    /// <summary>验证码位数。</summary>
    public const int Digits = 6;

    /// <summary>密钥长度（字节），与 HMAC-SHA1 的输出等长。</summary>
    public const int SecretBytes = 20;

    /// <summary>步长（秒）。</summary>
    public const int StepSeconds = 30;

    /// <summary>前后各容忍几个步长：手机与服务器的时钟总有偏差，输入也要时间。</summary>
    public const int AllowedDrift = 1;

    /// <summary>生成一个新的随机密钥。</summary>
    public static byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>某一时刻所在的步序号。</summary>
    public static long TimeStepAt(DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(now, DateTimeOffset.UnixEpoch);
        return now.ToUnixTimeSeconds() / StepSeconds;
    }

    /// <summary>计算某一步的验证码。</summary>
    public static string ComputeCode(byte[] secret, long timeStep)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length == 0) throw new ArgumentException("Secret must not be empty.", nameof(secret));
        ArgumentOutOfRangeException.ThrowIfNegative(timeStep);
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, timeStep);

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);

        // RFC 4226 §5.3 动态截断
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                     | (hash[offset + 1] << 16)
                     | (hash[offset + 2] << 8)
                     | hash[offset + 3];

        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>校验验证码，返回命中的步序号；不匹配或该步已用过时返回 null。</summary>
    /// <remarks>容忍空格；拒绝不大于 lastUsedStep 的步，消费与并发控制由调用方负责。</remarks>
    public static long? Verify(byte[] secret, string code, DateTimeOffset now, long? lastUsedStep)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length == 0) throw new ArgumentException("Secret must not be empty.", nameof(secret));
        ArgumentNullException.ThrowIfNull(code);
        var normalized = code.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (normalized.Length != Digits || !normalized.All(char.IsAsciiDigit))
            return null;

        var current = TimeStepAt(now);
        for (var step = Math.Max(0, current - AllowedDrift); step <= current + AllowedDrift; step++)
        {
            if (lastUsedStep is { } used && step <= used)
                continue;

            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(ComputeCode(secret, step)),
                    Encoding.ASCII.GetBytes(normalized)))
            {
                return step;
            }
        }

        return null;
    }

    /// <summary>导出大写、无填充的 Base32 密钥。</summary>
    public static string FormatSecret(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length == 0) throw new ArgumentException("Secret must not be empty.", nameof(secret));
        return Base32.Encode(secret);
    }

    /// <summary>导入 Base32 密钥；容忍大小写、空白、分隔符和尾填充，非法编码返回 null。</summary>
    public static byte[]? ParseSecret(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Base32.Decode(text);
    }

    /// <summary>生成身份验证器应用的 otpauth 地址。</summary>
    public static string BuildUri(string issuer, string account, byte[] secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        var label = Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(account);
        return $"otpauth://totp/{label}?secret={FormatSecret(secret)}&issuer={Uri.EscapeDataString(issuer)}"
               + $"&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }
}
