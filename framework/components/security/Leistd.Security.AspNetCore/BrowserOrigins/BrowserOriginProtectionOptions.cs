namespace Leistd.Security.AspNetCore.BrowserOrigins;

/// <summary>宿主显式选择来源防护的路径与原生 CORS 策略。</summary>
public sealed class BrowserOriginProtectionOptions
{
    /// <summary>默认配置节。</summary>
    public const string SectionName = "Leistd:Security:BrowserOrigins";

    /// <summary>检查非安全 HTTP 方法的路径前缀；默认空，/ 覆盖全部路径。</summary>
    public string[] WritePaths { get; set; } = [];

    /// <summary>检查全部方法的路径前缀，适用于 WebSocket 握手；默认空。</summary>
    public string[] AllMethodPaths { get; set; } = [];

    /// <summary>无端点策略时使用的原生策略名；null 使用默认 CORS 策略。</summary>
    public string? CorsPolicyName { get; set; }
}
