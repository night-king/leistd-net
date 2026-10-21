namespace Leistd.Settings.Definitions;

/// <summary>向定义上下文登记设置定义。</summary>
/// <example>
/// <code>
/// public class DisplaySettingDefinitionProvider : ISettingDefinitionProvider
/// {
///     public void Define(ISettingDefinitionContext context)
///     {
///         context.Add(
///                    "Display.TimeZone",
///                    defaultValue: "Asia/Shanghai",
///                    scopes: SettingScopes.All,
///                    group: "Display")
///                .IsVisibleToClients = true;
///
///         // 只允许租户级覆盖，且不下发客户端
///         context.Add("Export.MaxRowsPerFile", defaultValue: "50000", scopes: SettingScopes.Tenant);
///     }
/// }
/// </code>
/// </example>
public interface ISettingDefinitionProvider
{
    /// <summary>定义设置。</summary>
    /// <param name="context">设置定义上下文。</param>
    void Define(ISettingDefinitionContext context);

    /// <summary>在全部提供者的 <see cref="Define"/> 之后调用，用于补充或调整别处声明的定义。</summary>
    /// <remarks>提供者之间没有执行顺序约定，修改别处声明的定义须放在这里。</remarks>
    /// <param name="context">设置定义上下文。</param>
    void PostDefine(ISettingDefinitionContext context)
    {
    }
}

/// <summary>设置定义的登记与查询上下文。</summary>
/// <remarks>设置名全局唯一，重复注册在首次访问定义时抛出异常。</remarks>
public interface ISettingDefinitionContext
{
    /// <summary>添加一项设置定义。</summary>
    /// <param name="name">设置名称，全局唯一。</param>
    /// <param name="defaultValue">代码默认值。</param>
    /// <param name="scopes">允许覆盖该设置的层级；默认只允许租户级。</param>
    /// <param name="displayName">显示名称。</param>
    /// <param name="group">所属分组的稳定标识。</param>
    ISettingDefinition Add(
        string name,
        string? defaultValue = null,
        SettingScopes scopes = SettingScopes.Tenant,
        string? displayName = null,
        string? group = null);

    /// <summary>获取设置定义；不存在时返回 <see langword="null"/>。</summary>
    /// <param name="name">设置名称。</param>
    ISettingDefinition? GetOrNull(string name);
}
