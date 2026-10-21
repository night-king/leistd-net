namespace Leistd.Redaction;

/// <summary>把敏感值换成可安全输出的形态，用于写日志与对外展示。</summary>
/// <remarks>
/// <para>要不要脱敏、对谁脱敏由调用方决定，本类只提供形态。手机号、证件号、卡号等用 <see cref="RedactPartially"/> 按业务传保留位数。</para>
/// <para>掩码固定为三个星号，不按原值长度补齐，不暴露原值长度。</para>
/// </remarks>
public static class TextRedactor
{
    private const string Mask = "***";

    // 本地部最多保留的字符数
    private const int MaxKeptLocalChars = 3;

    /// <summary>邮箱脱敏：保留本地部开头的几位与完整域名，例如 <c>al***@example.com</c>。</summary>
    /// <param name="address">邮箱地址；可为 <see langword="null"/>。</param>
    /// <returns>脱敏后的地址；<paramref name="address"/> 为空时返回空串。</returns>
    /// <remarks>
    /// <para>本地部从第一个字母或数字起最多留 3 位，且不超过可用长度的一半；域名完整保留，便于按域名聚合排障。</para>
    /// <para>按最后一个 <c>'@'</c> 切分（本地部可用引号包住 <c>'@'</c>）。拿不到域名时整体按本地部处理，不原样返回。</para>
    /// </remarks>
    public static string RedactEmail(string? address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return string.Empty;
        }

        var value = address.AsSpan();
        var at = value.LastIndexOf('@');

        // at == 0 也算有域名：@example.com 没有本地部，但域名照样有排障价值
        var hasDomain = at >= 0 && at < value.Length - 1;
        var local = hasDomain ? value[..at] : (at == 0 ? [] : value);
        var domain = hasDomain ? value[at..] : default;

        return string.Concat(KeptLocalChars(local), Mask, domain);
    }

    /// <summary>部分脱敏：保留开头与结尾各若干字符，中间换成 <c>***</c>，例如 <c>158***90</c>。</summary>
    /// <param name="value">要脱敏的值；可为 <see langword="null"/>。</param>
    /// <param name="keepStart">开头保留的字符数，不能为负。</param>
    /// <param name="keepEnd">结尾保留的字符数，不能为负。</param>
    /// <returns>脱敏后的值；<paramref name="value"/> 为空时返回空串。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="keepStart"/> 或 <paramref name="keepEnd"/> 为负。</exception>
    /// <remarks>
    /// 长度不足以同时保留两端时整体返回 <c>***</c>，不泄露短值。
    /// </remarks>
    /// <example>
    /// <code>
    /// TextRedactor.RedactPartially(phone, keepStart: 3, keepEnd: 2);   // 158***90
    /// TextRedactor.RedactPartially(pan, keepStart: 6, keepEnd: 4);     // 123456***1121
    /// </code>
    /// </example>
    public static string RedactPartially(string? value, int keepStart, int keepEnd)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keepStart);
        ArgumentOutOfRangeException.ThrowIfNegative(keepEnd);

        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= keepStart + keepEnd
            ? Mask
            : string.Concat(value.AsSpan(0, keepStart), Mask, value.AsSpan(value.Length - keepEnd));
    }

    // 从第一个字母或数字起最多留 MaxKeptLocalChars 位，且不超过可用长度的一半，短本地部不会几乎全暴露
    private static ReadOnlySpan<char> KeptLocalChars(ReadOnlySpan<char> local)
    {
        var start = -1;
        for (var i = 0; i < local.Length; i++)
        {
            if (char.IsLetterOrDigit(local[i]))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return default;
        }

        var available = local.Length - start;
        var kept = Math.Min(MaxKeptLocalChars, available / 2);
        return kept == 0 ? default : local.Slice(start, kept);
    }
}
