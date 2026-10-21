namespace Leistd.Notifications.Email.Options;

/// <summary>通知邮件渠道的选项。</summary>
public sealed class EmailNotificationOptions
{
    /// <summary>默认配置节。</summary>
    public const string SectionName = "Leistd:Notifications:Email";

    /// <summary>站点对外地址，用于把站内相对链接转成邮件里的绝对地址。</summary>
    /// <remarks>
    /// <para>可以不配：不配时相对链接不附进邮件，绝对 http(s) 链接照常附上。配置时须是绝对 http(s) 地址。</para>
    /// <para>以 <c>/</c> 开头的链接直接拼在它后面（去掉末尾的 <c>/</c>），因此带路径前缀的站点写 <c>https://example.com/portal</c>，
    /// 前端用哈希路由时写 <c>https://example.com/#</c>。不从当前请求推导。</para>
    /// </remarks>
    public string? PublicBaseUrl { get; set; }
}
