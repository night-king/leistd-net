namespace Leistd.Settings.Definitions;

/// <summary>设置值的类型。</summary>
/// <remarks>设置值一律以字符串存储，类型只决定写入时接受哪些写法。</remarks>
public enum SettingValueType
{
    /// <summary>任意非空文本。</summary>
    Text = 0,

    /// <summary>只接受 <c>true</c> / <c>false</c>（小写）。</summary>
    Boolean = 1,

    /// <summary>不变文化下的 32 位整数，受 <see cref="ISettingDefinition.Minimum"/> 与 <see cref="ISettingDefinition.Maximum"/> 约束。</summary>
    Integer = 2
}
