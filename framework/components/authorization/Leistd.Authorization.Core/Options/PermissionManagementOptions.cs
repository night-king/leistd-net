namespace Leistd.Authorization.Options;

/// <summary>权限管理用例的展示约定。</summary>
public sealed class PermissionManagementOptions
{
    /// <summary>翻译权限与分组显示名的资源类型；<see langword="null"/> 时不翻译。</summary>
    /// <remarks>
    /// 词条键为 <c>Permission:{权限名}</c> 与 <c>PermissionGroup:{组名}</c>；查不到时回落到定义里的 <c>DisplayName</c>，再回落到名称。
    /// </remarks>
    public Type? LocalizationResource { get; set; }
}
