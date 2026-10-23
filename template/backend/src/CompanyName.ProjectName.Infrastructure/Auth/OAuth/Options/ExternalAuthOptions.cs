#if (ExternalLogin)
namespace CompanyName.ProjectName.Infrastructure.Auth.OAuth.Options;

/// <summary>外部身份提供商的启用开关与客户端凭据。</summary>
/// <remarks>
/// 部署配置给基线，宿主管理员可在「系统设置 → 外部登录」里运行期覆盖（经宿主级设置配置源），
/// 清除覆盖值即回到部署基线。客户端凭据由环境变量或 user-secrets 注入。
/// 因此消费方读 <c>IOptionsMonitor</c>。不配也能启动：开关默认关闭；开关打开但凭据不全时该提供方不可用，
/// 启动与运行期一样只记警告，不阻止启动、也不让同批的其他宿主设置回滚。
/// </remarks>
public sealed class ExternalAuthOptions
{
    public const string SectionName = "ExternalAuth";

    public ProviderOptions Github { get; } = new();

    public ProviderOptions Google { get; } = new();

    public sealed class ProviderOptions
    {
        /// <summary>启用意图；是否真的提供入口还要看凭据是否齐全，见 <see cref="IsAvailable"/>。</summary>
        public bool Enabled { get; set; }

        public string? ClientId { get; set; }

        public string? ClientSecret { get; set; }

        public bool HasClientId => !string.IsNullOrWhiteSpace(ClientId);

        public bool HasClientSecret => !string.IsNullOrWhiteSpace(ClientSecret);

        /// <summary>开关打开且两项凭据齐全：登录页提供该入口，认证方案处于登记状态。</summary>
        public bool IsAvailable => Enabled && HasClientId && HasClientSecret;
    }
}
#endif
