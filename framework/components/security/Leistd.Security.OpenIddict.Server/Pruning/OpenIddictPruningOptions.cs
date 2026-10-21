namespace Leistd.Security.OpenIddict.Server.Pruning;

/// <summary>OpenIddict 令牌和授权清理的最小保留期。</summary>
public sealed class OpenIddictPruningOptions
{
    /// <summary>默认配置节。</summary>
    public const string SectionName = "Leistd:Security:OpenIddict:Pruning";
    /// <summary>默认14天、至少10分钟；与原生 Quartz 的默认值及支持下限一致。</summary>
    public TimeSpan MinimumRetention { get; set; } = TimeSpan.FromDays(14);
}
