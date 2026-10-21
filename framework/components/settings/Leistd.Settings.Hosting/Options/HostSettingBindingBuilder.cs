using System.Globalization;

namespace Leistd.Settings.Hosting.Options;

/// <summary>声明哪些宿主级设置覆盖哪些配置键。</summary>
/// <remarks>
/// <para>设置有值时，它绑定的每个配置键都写成这个值；没有值的设置不出现在配置源里，自然回落到部署配置。
/// 被绑定的设置必须已定义且是进程级（<c>SettingScopes.Host</c>），否则首次访问定义时抛出。</para>
/// <para>同一设置名重复绑定时，设置名、按顺序的配置键、兜底值与选项类型全部相同即视为同一登记、不重复生效；
/// 任一项不同（只换兜底值或只换键的顺序也算）在绑定时抛出 <see cref="InvalidOperationException"/>，消息列出两处登记。</para>
/// <para>被绑定设置的代码默认值由组件改写成<b>部署基线</b>（部署配置里这些键的值），清除设置即回到部署配置。
/// 基线按定义的值元数据归一（布尔小写、候选值取定义里的写法），认不出时用兜底值；机密设置没有默认值。</para>
/// </remarks>
public sealed class HostSettingBindingBuilder
{
    private readonly List<HostSettingBinding> _bindings;

    internal HostSettingBindingBuilder(List<HostSettingBinding> bindings) => _bindings = bindings;

    /// <summary>把设置绑定到一组配置键。</summary>
    /// <param name="settingName">宿主级设置名。</param>
    /// <param name="configurationKeys">被覆盖的配置键；同一个值有多种写法时全部列出。</param>
    /// <param name="fallback">哪个配置源都没有这些键时的部署基线。</param>
    public HostSettingBindingBuilder Bind(string settingName, IEnumerable<string> configurationKeys, string? fallback = null)
        => Add(settingName, configurationKeys, fallback, optionsType: null);

    private HostSettingBindingBuilder Add(string settingName, IEnumerable<string> configurationKeys, string? fallback, Type? optionsType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingName);
        ArgumentNullException.ThrowIfNull(configurationKeys);
        IReadOnlyList<string> keys = [.. configurationKeys];
        if (keys.Count == 0 || keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty configuration key is required.", nameof(configurationKeys));
        }

        var binding = new HostSettingBinding(settingName, keys, fallback, optionsType);
        if (_bindings.Find(existing => existing.SettingName == settingName) is not { } registered)
        {
            _bindings.Add(binding);
        }
        else if (!registered.Matches(binding))
        {
            throw new InvalidOperationException(
                $"Host setting '{settingName}' is already bound to {registered.Describe()}; it cannot also be bound to {binding.Describe()}.");
        }

        return this;
    }

    /// <summary>把设置绑定到某个选项类的属性：配置键为 <c>{节名}:{属性名}</c>，兜底值取该类型上的属性默认值。</summary>
    /// <remarks>
    /// 应用新值后会按该选项类型（默认名称）的校验规则整组检验，不合规就整组不生效、沿用上一组；
    /// 只用 <see cref="Bind"/> 绑定的键不参与这项检验。
    /// </remarks>
    /// <typeparam name="TOptions">选项类型。</typeparam>
    /// <param name="settingName">宿主级设置名。</param>
    /// <param name="sectionName">选项绑定的配置节。</param>
    /// <param name="propertyName">属性名，用 <c>nameof</c> 取，改名时编译期就能发现映射断了。</param>
    public HostSettingBindingBuilder BindOption<TOptions>(string settingName, string sectionName, string propertyName)
        where TOptions : class, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);
        var property = typeof(TOptions).GetProperty(propertyName)
            ?? throw new ArgumentException($"{typeof(TOptions).Name} has no property '{propertyName}'.", nameof(propertyName));

        return Add(settingName, [$"{sectionName}:{property.Name}"], Format(property.GetValue(new TOptions())), typeof(TOptions));
    }

    private static string? Format(object? value) => value switch
    {
        null => null,
        bool flag => flag ? "true" : "false",
        string text => string.IsNullOrEmpty(text) ? null : text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}

internal sealed record HostSettingBinding(
    string SettingName,
    IReadOnlyList<string> ConfigurationKeys,
    string? Fallback,
    Type? OptionsType)
{
    // 相同登记：配置键按顺序逐个相等，兜底值与选项类型也相等
    public bool Matches(HostSettingBinding other)
        => SettingName == other.SettingName
            && ConfigurationKeys.SequenceEqual(other.ConfigurationKeys, StringComparer.Ordinal)
            && Fallback == other.Fallback
            && OptionsType == other.OptionsType;

    public string Describe()
        => $"keys [{string.Join(", ", ConfigurationKeys)}], fallback {(Fallback is null ? "none" : $"'{Fallback}'")}, "
            + $"options type {OptionsType?.FullName ?? "none"}";
}

// 多次 AddHostSettings 的绑定在登记时就并入这里，作为单例实例注册，不经选项配置回调
internal sealed class HostSettingBindingCollection
{
    public List<HostSettingBinding> Bindings { get; } = [];
}
