namespace Leistd.Security.OneTimeCodes.VerificationCodes;

/// <summary>验证码摘要密钥；跨实例、跨重启保持稳定。</summary>
public sealed class VerificationCodeOptions
{
    /// <summary>配置节名</summary>
    public const string SectionName = "Leistd:Security:VerificationCodes";

    /// <summary>摘要密钥（Base64）。无默认值</summary>
    public string? Key { get; set; }

    /// <summary>最小密钥长度（字节）</summary>
    public const int MinimumKeyBytes = 32;

    /// <summary>密钥是否可用</summary>
    public bool IsKeyUsable => TryGetKeyBytes(out _);

    // 解析密钥字节
    internal bool TryGetKeyBytes(out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(Key))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(Key);
            if (bytes.Length < MinimumKeyBytes)
            {
                return false;
            }

            key = bytes;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
