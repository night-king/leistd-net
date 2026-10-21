using System.Globalization;
using Leistd.Localization.Options;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Leistd.Localization.Json;

/// <summary>基于嵌入 JSON 资源的 <see cref="IStringLocalizer"/> 实现。</summary>
/// <remarks>
/// 查表按 <see cref="CultureInfo.CurrentUICulture"/> 逐级回落（如 <c>zh-Hans-CN</c> → <c>zh-Hans</c> → <c>zh</c>），
/// 再回落到 <see cref="JsonLocalizationOptions.DefaultCulture"/>；仍未命中则返回键本身。
/// 参数化通过标准 <see cref="string.Format(IFormatProvider?, string, object?[])"/>（位置占位 <c>{0}</c>）。
/// </remarks>
public sealed class JsonStringLocalizer(
    JsonLocalizationResourceReader reader,
    IOptions<JsonLocalizationOptions> options) : IStringLocalizer
{
    private readonly JsonLocalizationOptions _options = options.Value;

    /// <summary>按键取文案；未命中时返回键本身，且 <c>ResourceNotFound</c> 为 <see langword="true"/>。</summary>
    /// <param name="name">文案键。</param>
    public LocalizedString this[string name]
    {
        get
        {
            var value = Find(name);
            return new LocalizedString(name, value ?? name, resourceNotFound: value is null);
        }
    }

    /// <summary>按键取文案并填充位置参数；未命中时以键本身作为格式串。</summary>
    /// <param name="name">文案键。</param>
    /// <param name="arguments">格式化参数。</param>
    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var format = Find(name);
            var value = string.Format(CultureInfo.CurrentCulture, format ?? name, arguments);
            return new LocalizedString(name, value, resourceNotFound: format is null);
        }
    }

    /// <inheritdoc />
    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var culture in CultureChain())
        {
            foreach (var pair in reader.GetTexts(culture))
            {
                if (seen.Add(pair.Key))
                    yield return new LocalizedString(pair.Key, pair.Value, resourceNotFound: false);
            }

            if (!includeParentCultures)
                break;
        }
    }

    private string? Find(string name)
    {
        foreach (var culture in CultureChain())
        {
            if (reader.GetTexts(culture).TryGetValue(name, out var value))
                return value;
        }

        return null;
    }

    // 当前 UI culture 的回落链，末尾追加默认语言。
    private IEnumerable<string> CultureChain()
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var culture = CultureInfo.CurrentUICulture;
             !culture.Equals(CultureInfo.InvariantCulture);
             culture = culture.Parent)
        {
            if (visited.Add(culture.Name) && culture.Name.Length > 0)
                yield return culture.Name;
        }

        if (visited.Add(_options.DefaultCulture))
            yield return _options.DefaultCulture;
    }
}
