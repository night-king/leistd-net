using System.Text;
namespace Leistd.Security.OneTimeCodes;

// RFC 4648 Base32（不带填充）。身份验证器应用按这种写法导入密钥。
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    // 编码为大写 Base32，不带 = 填充。
    public static string Encode(ReadOnlySpan<byte> data)
    {
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return output.ToString();
    }

    // 解码；忽略大小写、空格与填充。含非法字符时返回 null。
    public static byte[]? Decode(string text)
    {
        var normalized = new string(text.Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray());
        var unpadded = normalized.TrimEnd('=');
        if (normalized.Length != unpadded.Length &&
            (normalized.Length % 8 != 0 || normalized.Length - unpadded.Length != (8 - unpadded.Length % 8) % 8)) return null;
        var output = new List<byte>(unpadded.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in unpadded)
        {
            var index = Alphabet.IndexOf(char.ToUpperInvariant(c));
            if (index < 0)
                return null;

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        var bytes = output.ToArray();
        return bytes.Length != 0 && Encode(bytes) == unpadded ? bytes : null;
    }
}
