using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Leistd.Localization.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Leistd.Localization.Json;

/// <summary>读取并缓存随程序集嵌入的 JSON 本地化资源。</summary>
/// <remarks>
/// 资源文件结构：<c>{ "culture": "en", "texts": { "Key": "Value" } }</c>；
/// 缺 <c>culture</c> 段的文件被忽略。同一 culture 的键在多个程序集出现时，
/// 按 <see cref="JsonLocalizationOptions.ResourceAssemblies"/> 顺序后者覆盖前者。
/// 每个 culture 的合并结果按需加载一次并缓存。
/// </remarks>
public sealed class JsonLocalizationResourceReader(
    IOptions<JsonLocalizationOptions> options,
    ILogger<JsonLocalizationResourceReader>? logger = null)
{
    private readonly JsonLocalizationOptions _options = options.Value;
    private readonly ILogger _logger = logger ?? NullLogger<JsonLocalizationResourceReader>.Instance;
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _cache = new();

    /// <summary>取指定 culture 的全部键值（已合并各程序集、已缓存）；未找到资源时返回空字典。</summary>
    public IReadOnlyDictionary<string, string> GetTexts(string culture)
        => _cache.GetOrAdd(culture, LoadCulture);

    private IReadOnlyDictionary<string, string> LoadCulture(string culture)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var assembly in _options.ResourceAssemblies)
        {
            var resourceName = ResolveResourceName(assembly, culture);
            if (resourceName is null)
                continue;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
                continue;

            var parsed = Parse(stream, culture, resourceName);
            if (parsed is null)
                continue;

            // 后登记的程序集覆盖先前的同名键
            foreach (var pair in parsed)
                texts[pair.Key] = pair.Value;
        }

        return texts;
    }

    // 按资源路径和 culture 精确匹配嵌入资源后缀；多个候选视为歧义并抛异常。
    private string? ResolveResourceName(Assembly assembly, string culture)
    {
        var pathHint = _options.ResourcesPath.Replace('/', '.').Replace('\\', '.').Trim('.');
        var suffix = string.IsNullOrEmpty(pathHint)
            ? $".{culture}.json"
            : $".{pathHint}.{culture}.json";

        var matches = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Multiple localization resource candidates matched culture '{culture}' in assembly '{assembly.GetName().Name}': " +
                $"{string.Join(", ", matches)}. Ensure the logical suffix '{suffix}' is unique within a single assembly.");
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    private IReadOnlyDictionary<string, string>? Parse(Stream stream, string expectedCulture, string resourceName)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stream);
        }
        catch (JsonException ex)
        {
            // 坏文件跳过并告警，其余程序集与键正常加载
            _logger.LogWarning(ex, "Localization resource {ResourceName} is not valid JSON; skipped.", resourceName);
            return null;
        }

        using (document)
        {
            var root = document.RootElement;

            // 缺 culture 段的文件忽略
            if (!root.TryGetProperty("culture", out var cultureElement)
                || cultureElement.ValueKind != JsonValueKind.String)
                return null;

            // 声明的 culture 与文件名不一致时告警但仍加载
            var declaredCulture = cultureElement.GetString();
            if (!string.Equals(declaredCulture, expectedCulture, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Localization resource {ResourceName} declares culture '{Declared}' which does not match the file-name culture '{Expected}'; please verify.",
                    resourceName,
                    declaredCulture,
                    expectedCulture);
            }

            if (!root.TryGetProperty("texts", out var textsElement)
                || textsElement.ValueKind != JsonValueKind.Object)
                return null;

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in textsElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    result[property.Name] = property.Value.GetString()!;
            }

            return result;
        }
    }
}
