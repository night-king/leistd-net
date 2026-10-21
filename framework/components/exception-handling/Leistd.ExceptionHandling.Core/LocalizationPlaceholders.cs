namespace Leistd.ExceptionHandling;

/// <summary>按 <see cref="BusinessException.LocalizationData"/> 的约定填充资源文案里的具名占位符。</summary>
/// <remarks>
/// 错误响应与其他按错误码展示原因的地方（如操作记录的失败原因）共用这一处，
/// 同一个码在两处渲染出的句子才一致。
/// </remarks>
public static class LocalizationPlaceholders
{
    /// <summary>把文案中的 <c>{Name}</c> 替换为同名参数值。</summary>
    /// <remarks>
    /// 名称按序数比较、区分大小写；值按 <see cref="object.ToString"/> 取文本，<see langword="null"/> 视为空串。
    /// 没有对应参数的占位符原样保留。
    /// </remarks>
    /// <param name="text">资源文案。</param>
    /// <param name="data">占位参数；为 <see langword="null"/> 或为空时原样返回 <paramref name="text"/>。</param>
    /// <returns>填充后的文案。</returns>
    public static string Fill(string text, IEnumerable<KeyValuePair<string, object?>>? data)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (data is null)
            return text;

        foreach (var pair in data)
            text = text.Replace("{" + pair.Key + "}", pair.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
        return text;
    }
}
