namespace Leistd.Security.AspNetCore.Cookies;

/// <summary>同宿主各 Cookie 方案共用的票据缓存配置。</summary>
public sealed class DistributedTicketStoreOptions
{
    /// <summary>默认配置节。</summary>
    public const string SectionName = "Leistd:Security:Tickets";

    /// <summary>缓存键前缀。</summary>
    public string KeyPrefix { get; set; } = "Leistd:AuthTicket:";

    /// <summary>票据未指定到期时间时的寿命。</summary>
    public TimeSpan FallbackLifetime { get; set; } = TimeSpan.FromMinutes(5);
}
