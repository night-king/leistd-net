namespace Leistd.Security.OpenIddict.Validation.SigningKeys;

/// <summary>未知签名键刷新时的等待与每副本限频。</summary>
public sealed class SigningKeyRefreshOptions
{
    /// <summary>默认配置节。</summary>
    public const string SectionName = "Leistd:Security:OpenIddict:SigningKeys";
    /// <summary>原生 HTTP 抓取与当前请求等待的上限，默认10秒。</summary>
    public TimeSpan FetchTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>两次转交请求刷新之间的最短间隔，默认1分钟；自动刷新不受影响。</summary>
    public TimeSpan MinimumInterval { get; set; } = TimeSpan.FromMinutes(1);
}
