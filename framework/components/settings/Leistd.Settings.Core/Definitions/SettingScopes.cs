namespace Leistd.Settings.Definitions;

/// <summary>设置值允许存放的层级。</summary>
/// <remarks>
/// 代码默认值不在其中：它由 <c>ISettingDefinition.DefaultValue</c> 承载，任何设置都有。
/// </remarks>
[Flags]
public enum SettingScopes
{
    /// <summary>不允许覆盖，只能用代码默认值。</summary>
    None = 0,

    /// <summary>租户级；宿主视角（<c>TenantId</c> 为 <see langword="null"/>）也走这一层。</summary>
    Tenant = 1,

    /// <summary>用户级，优先于租户级。</summary>
    User = 2,

    /// <summary>租户级与用户级都允许。</summary>
    All = Tenant | User,

    /// <summary>仅宿主级：整个进程只有一份值，租户与用户都不能覆盖。</summary>
    /// <remarks>
    /// 用于日志级别这类一个进程只有一份的配置。不与其它层级组合；只能在宿主上下文写入，租户上下文的写入被拒绝。
    /// </remarks>
    Host = 4
}
