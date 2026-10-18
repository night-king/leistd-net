namespace CompanyName.ProjectName.Application.Settings.Options;

/// <summary>界面显示的部署基线：组织与个人都没设置时采用的默认值，所有组织共用。</summary>
/// <remarks>
/// <para>它是 <c>Display.*</c> 设置的代码默认值：解析顺序是个人 → 组织 → 这里 → 设备（浏览器端）或请求文化与 UTC（服务端）。
/// 空表示不给默认值，交给设备决定——同一组织里语言、时区各不相同时，这比任何统一默认都准。</para>
/// <para>启动期基线：设置定义在进程内只构建一次，改了要重启各实例，且各实例配置保持一致。</para>
/// </remarks>
public class DisplayDefaultsOptions
{
    public const string SectionName = "DisplayDefaults";
#if (IncludeLocalization)

    /// <summary>默认界面语言，取值见 <c>SettingConstant.Display.SupportedLanguages</c>；空为跟随设备。</summary>
    public string? Language { get; set; }
#endif

    /// <summary>默认展示时区（IANA 名，如 <c>Asia/Tokyo</c>）；空为跟随设备。</summary>
    public string? TimeZone { get; set; }
}
