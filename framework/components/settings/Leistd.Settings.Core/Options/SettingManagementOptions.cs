namespace Leistd.Settings.Options;

/// <summary>设置管理用例的展示约定。</summary>
public sealed class SettingManagementOptions
{
    /// <summary>翻译设置名与分组名的资源类型；<see langword="null"/> 时不翻译，直接用定义里的文案。</summary>
    /// <remarks>
    /// 词条键按约定推导：设置为 <c>Setting:{设置名}</c>，分组为 <c>SettingGroup:{分组标识}</c>。
    /// 查不到词条时回落到定义里的 <c>DisplayName</c>（再没有就用设置名）与分组标识本身。
    /// </remarks>
    public Type? LocalizationResource { get; set; }

    /// <summary>未写分组的设置归入的分组标识，默认 <c>Other</c>。</summary>
    public string DefaultGroup { get; set; } = "Other";
}
