using System.Security.Cryptography;
using System.Text;

namespace Leistd.Security.OneTimeCodes;

/// <summary>生成每组 10 个、每个 80 位随机的恢复码，使用 SHA-256 摘要存储。</summary>
/// <remarks>调用方只展示一次明文，并原子消费已保存摘要；组件不保存消费状态。</remarks>
public static class RecoveryCodes
{
    /// <summary>每次生成的个数。</summary>
    public const int Count = 10;

    private const int Length = 16;
    private const int GroupSize = 4;
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>生成一组新的恢复码（明文，只在生成时展示一次）。</summary>
    public static IReadOnlyList<string> Generate()
    {
        var codes = new List<string>(Count);
        for (var i = 0; i < Count; i++)
        {
            var chars = new char[Length];
            for (var j = 0; j < Length; j++)
            {
                chars[j] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }

            codes.Add(string.Join('-', Enumerable.Range(0, Length / GroupSize)
                .Select(g => new string(chars, g * GroupSize, GroupSize))));
        }

        return codes;
    }

    /// <summary>去掉空白与分隔符、转小写后计算 SHA-256 摘要。</summary>
    public static string Hash(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var normalized = new string(code
            .Where(c => !char.IsWhiteSpace(c) && c != '-')
            .Select(char.ToLowerInvariant)
            .ToArray());
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(normalized)));
    }
}
