using Leistd.EventBus.Events;

namespace Leistd.Authorization.Events;

/// <summary>某个主体的授予经管理用例整体替换。</summary>
/// <remarks>
/// 由管理用例在替换成功后发布，宿主据此记审计；种子与单项授予不发布。
/// </remarks>
/// <param name="providerName">授予对象类型。</param>
/// <param name="providerKey">授予对象 Key。</param>
/// <param name="subjectDisplayName">主体显示名快照；取不到时为 <see langword="null"/>。</param>
/// <param name="version">替换后的授权版本。</param>
public sealed class PermissionGrantsReplacedEvent(
    string providerName,
    string providerKey,
    string? subjectDisplayName,
    long version) : LocalEvent
{
    /// <summary>授予对象类型。</summary>
    public string ProviderName { get; } = providerName;

    /// <summary>授予对象 Key。</summary>
    public string ProviderKey { get; } = providerKey;

    /// <summary>主体显示名快照。</summary>
    public string? SubjectDisplayName { get; } = subjectDisplayName;

    /// <summary>替换后的授权版本。</summary>
    public long Version { get; } = version;
}
