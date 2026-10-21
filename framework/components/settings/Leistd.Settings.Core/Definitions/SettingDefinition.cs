namespace Leistd.Settings.Definitions;

internal sealed class SettingDefinition : ISettingDefinition
{
    public SettingDefinition(
        string name,
        string? defaultValue,
        SettingScopes scopes,
        string? displayName,
        string? group)
    {
        // 进程级与可分层覆盖互斥（Host | User 没有一致的读取语义），在构造处拒绝
        if (scopes.HasFlag(SettingScopes.Host) && scopes != SettingScopes.Host)
        {
            throw new ArgumentException(
                $"Setting '{name}' declares '{scopes}': the host scope is exclusive and cannot be "
                + "combined with the tenant or user scope.",
                nameof(scopes));
        }

        Name = name;
        DefaultValue = defaultValue;
        Scopes = scopes;
        DisplayName = displayName;
        Group = group;
    }

    public string Name { get; }
    public string? DisplayName { get; set; }
    public string? DefaultValue { get; set; }
    public SettingScopes Scopes { get; }
    public string? Group { get; set; }
    public bool IsVisibleToClients { get; set; }

    public bool IsEncrypted { get; set; }

    public SettingValueType ValueType { get; set; }

    public int? Minimum { get; set; }

    public int? Maximum { get; set; }

    public IReadOnlyList<string>? AllowedValues { get; set; }
}

// 设置定义上下文：名称全局唯一，重复注册在首次访问定义时失败。
internal sealed class SettingDefinitionContext : ISettingDefinitionContext
{
    private readonly Dictionary<string, SettingDefinition> _settings = new(StringComparer.Ordinal);

    public ISettingDefinition Add(
        string name,
        string? defaultValue = null,
        SettingScopes scopes = SettingScopes.Tenant,
        string? displayName = null,
        string? group = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var definition = new SettingDefinition(name, defaultValue, scopes, displayName, group);
        if (!_settings.TryAdd(name, definition))
        {
            throw new InvalidOperationException(
                $"Setting '{name}' already exists; setting names must be globally unique.");
        }

        return definition;
    }

    public ISettingDefinition? GetOrNull(string name)
        => string.IsNullOrWhiteSpace(name) ? null : _settings.GetValueOrDefault(name);

    public IReadOnlyList<ISettingDefinition> GetAll() => [.. _settings.Values];
}
