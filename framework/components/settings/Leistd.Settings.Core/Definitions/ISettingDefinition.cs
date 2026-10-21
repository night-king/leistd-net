using Leistd.Settings.Resolution;

namespace Leistd.Settings.Definitions;

/// <summary>一项设置的定义。</summary>
public interface ISettingDefinition
{
    /// <summary>设置名称（全局唯一）。</summary>
    string Name { get; }

    /// <summary>显示名称。</summary>
    /// <remarks>框架原样返回，作为宿主词条缺失时的回落文案（见 <c>SettingManagementOptions.LocalizationResource</c>）。</remarks>
    string? DisplayName { get; set; }

    /// <summary>代码默认值；任何层级都没有值时返回它。</summary>
    string? DefaultValue { get; set; }

    /// <summary>允许覆盖该设置的层级。</summary>
    SettingScopes Scopes { get; }

    /// <summary>所属分组的稳定标识；<see langword="null"/> 表示未分组。</summary>
    /// <remarks>只承载分组标识，展示文案由宿主按标识查词条表。</remarks>
    string? Group { get; set; }

    /// <summary>是否允许下发到客户端，默认 <see langword="false"/>。</summary>
    bool IsVisibleToClients { get; set; }

    /// <summary>是否为机密设置：值落库前加密，读取时解密。</summary>
    /// <remarks>
    /// <para>用于口令、令牌一类的值。加解密用宿主的 Data Protection（<c>IDataProtectionProvider</c>）；
    /// 宿主没注册时，读写机密设置会失败，不会存成明文。</para>
    /// <para>机密设置永不下发客户端：即使标记了 <see cref="IsVisibleToClients"/>，
    /// <see cref="ISettingProvider.GetAllAsync"/> 按“仅客户端可见”读取时也不包含它；该标记只表示界面上可写。</para>
    /// <para>代码默认值不加密：机密设置的默认值应当为空，由宿主在未设置时自行回落（例如回落到配置文件）。</para>
    /// </remarks>
    bool IsEncrypted { get; set; }

    /// <summary>值的类型，写入时据此校验。</summary>
    /// <remarks>默认 <see cref="SettingValueType.Text"/>；界面也据此渲染控件。</remarks>
    SettingValueType ValueType { get; set; }

    /// <summary>整数设置的下界（含）；<see langword="null"/> 表示不限。</summary>
    int? Minimum { get; set; }

    /// <summary>整数设置的上界（含）；<see langword="null"/> 表示不限。</summary>
    int? Maximum { get; set; }

    /// <summary>允许的取值（按序号比较）；<see langword="null"/> 表示不限。</summary>
    /// <remarks>写入端据此拒绝候选之外的值，界面据此渲染下拉框。</remarks>
    IReadOnlyList<string>? AllowedValues { get; set; }
}
