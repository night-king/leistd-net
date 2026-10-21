#if (RemoteTokenAuth)
namespace CompanyName.ProjectName.Api.Options;

/// <summary>远端签发方配置（配置节 <c>Authentication</c>）。</summary>
/// <remarks>
/// <para>"什么样的签发方配置算可用"只在这里定义一次。<c>RemoteTokenAuthenticationExtensions</c>
/// 以它做启动期校验，并在解析 OpenIddict 校验器选项时取 issuer；
/// <c>RemoteIdentityReadinessInitializer</c> 运行期要拿它探发现文档。
/// 两处各自读一遍原始配置键、各自判一次空的话，规则就有两个版本。</para>
/// </remarks>
internal sealed class RemoteIdentityOptions
{
    /// <summary>配置节名</summary>
    public const string SectionName = "Authentication";

    /// <summary>签发方地址，必须是绝对 http(s) URI</summary>
    public string? Issuer { get; set; }

    /// <summary>本服务在令牌 <c>aud</c> 中的标识</summary>
    public string? Audience { get; set; }

#if (ResourceBrowserSession)
    /// <summary>浏览器会话使用的机密 OIDC 客户端标识</summary>
    public string? ClientId { get; set; }

    /// <summary>浏览器会话使用的机密 OIDC 客户端口令</summary>
    public string? ClientSecret { get; set; }

    /// <summary>浏览器会话申请本 API 的 scope，未配置时与 Audience 同名。</summary>
    public string? Scope { get; set; }

#endif

    /// <summary>解析后的签发方地址；不是合法绝对 http(s) URI 时为 <see langword="null"/></summary>
    public Uri? IssuerUri =>
        Uri.TryCreate(Issuer, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;

    /// <summary>配置是否可用</summary>
    public bool IsUsable => IssuerUri is not null && !string.IsNullOrWhiteSpace(Audience);

    /// <summary>发现文档地址。</summary>
    /// <remarks>仅在 <see cref="IsUsable"/> 成立后取用；启动期校验保证了这一点。</remarks>
    public Uri MetadataUrl => new(IssuerUri!, ".well-known/openid-configuration");
}
#endif
