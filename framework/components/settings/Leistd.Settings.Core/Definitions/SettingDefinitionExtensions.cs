namespace Leistd.Settings.Definitions;

/// <summary>声明设置值元数据的链式写法。</summary>
/// <example>
/// <code>
/// context.Add("Security.RequireTwoFactor", "false").AsBoolean();
/// context.Add("Security.LockoutDurationMinutes", "15").AsInteger(1, 1440);
/// context.Add("Logging.MinimumLevel", "Information", SettingScopes.Host).WithAllowedValues("Debug", "Information", "Warning");
/// </code>
/// </example>
public static class SettingDefinitionExtensions
{
    /// <summary>声明为布尔设置。</summary>
    /// <param name="definition">设置定义。</param>
    public static ISettingDefinition AsBoolean(this ISettingDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.ValueType = SettingValueType.Boolean;
        return definition;
    }

    /// <summary>声明为整数设置，并给出闭区间。</summary>
    /// <param name="definition">设置定义。</param>
    /// <param name="minimum">下界（含）。</param>
    /// <param name="maximum">上界（含）。</param>
    public static ISettingDefinition AsInteger(this ISettingDefinition definition, int minimum, int maximum)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimum, maximum);
        definition.ValueType = SettingValueType.Integer;
        definition.Minimum = minimum;
        definition.Maximum = maximum;
        return definition;
    }

    /// <summary>限定取值为给定候选之一。</summary>
    /// <param name="definition">设置定义。</param>
    /// <param name="values">候选值，不能为空。</param>
    public static ISettingDefinition WithAllowedValues(this ISettingDefinition definition, params IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(values);
        IReadOnlyList<string> allowed = [.. values];
        if (allowed.Count == 0)
        {
            throw new ArgumentException("At least one allowed value is required.", nameof(values));
        }

        definition.AllowedValues = allowed;
        return definition;
    }
}
