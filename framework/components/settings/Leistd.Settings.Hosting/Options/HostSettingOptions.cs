namespace Leistd.Settings.Hosting.Options;

/// <summary>宿主级设置的刷新周期。</summary>
/// <remarks>决定在别的实例上修改的设置多久在本实例生效。只来自部署配置，不做成设置项。</remarks>
public sealed class HostSettingOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "Leistd:Settings:Hosting";

    /// <summary>刷新周期，不小于 1 秒；默认 30 秒。</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);
}
