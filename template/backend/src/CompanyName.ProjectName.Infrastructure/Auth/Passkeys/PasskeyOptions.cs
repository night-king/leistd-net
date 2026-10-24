#if (LocalIdentity)
namespace CompanyName.ProjectName.Infrastructure.Auth.Passkeys;

/// <summary>通行密钥的依赖方（Relying Party）配置，配置节 <c>Passkeys</c>。</summary>
/// <remarks>
/// <para>是部署值，不是租户设置：凭据绑定在 RP ID（站点域名）上，登记之后改不得。多租户按子域名部署时 RP ID 取共同的父域。</para>
/// <para>没有配置（RP ID 或来源为空）时通行密钥不可用：设置页说明原因、登录页不给入口，宿主照常启动。
/// 不从请求的 Host 推断：Host 头可被伪造，按它签发的选项会把凭据绑到攻击者指定的域名上。</para>
/// </remarks>
public sealed class PasskeyOptions
{
    public const string SectionName = "Passkeys";

    /// <summary>RP ID：站点的可注册域名（不含协议与端口），如 <c>example.com</c>；本机开发为 <c>localhost</c>。</summary>
    public string? RelyingPartyId { get; set; }

    /// <summary>认证器与浏览器界面上显示的站点名称。</summary>
    public string RelyingPartyName { get; set; } = "MyProject";

    /// <summary>允许发起仪式的页面来源（协议 + 域名 + 端口），如 <c>https://app.example.com</c>；每个都须落在 RP ID 之下。</summary>
    public string[] Origins { get; set; } = [];

    /// <summary>非空的来源；部署清单里没填的来源注入的是空串，不算配置。</summary>
    public IEnumerable<string> ConfiguredOrigins => Origins.Where(origin => !string.IsNullOrWhiteSpace(origin)).Select(origin => origin.Trim());

    /// <summary>已配置，通行密钥可用。</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(RelyingPartyId) && ConfiguredOrigins.Any();
}
#endif
