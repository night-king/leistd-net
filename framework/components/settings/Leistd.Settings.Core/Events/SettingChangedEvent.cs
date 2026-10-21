using Leistd.EventBus.Events;
using Leistd.Settings.Definitions;
using Leistd.Settings.Management;

namespace Leistd.Settings.Events;

/// <summary>某一层级的设置值被写入或清除。</summary>
/// <remarks>
/// 由 <see cref="ISettingManager"/> 在写入后发布；在工作单元内发布时推迟到提交后分发，
/// 处理器看到的总是已经落库的值。事件不带值（避免机密设置明文进入事件），处理器需要时自己读。
/// </remarks>
/// <param name="name">设置名。</param>
/// <param name="scope">写入的层级。</param>
/// <param name="userId">用户级时的用户标识。</param>
public sealed class SettingChangedEvent(string name, SettingScopes scope, string? userId) : LocalEvent
{
    /// <summary>设置名。</summary>
    public string Name { get; } = name;

    /// <summary>写入的层级。</summary>
    public SettingScopes Scope { get; } = scope;

    /// <summary>用户级时的用户标识；其它层级为 <see langword="null"/>。</summary>
    public string? UserId { get; } = userId;
}
