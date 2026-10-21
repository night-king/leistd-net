namespace Leistd.Settings.Definitions;

/// <summary>汇总所有 <see cref="ISettingDefinitionProvider"/> 的定义并提供查询。</summary>
public interface ISettingDefinitionManager
{
    /// <summary>获取设置定义；未定义时返回 <see langword="null"/>。</summary>
    /// <param name="name">设置名称。</param>
    ISettingDefinition? GetOrNull(string name);

    /// <summary>获取全部设置定义。</summary>
    IReadOnlyList<ISettingDefinition> GetAll();
}
