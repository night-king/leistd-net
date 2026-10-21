#if (LocalIdentity)
namespace CompanyName.ProjectName.Domain.Auth.Options;

/// <summary>会话空闲时限的部署基线（组合根按会话 Cookie 的有效期配置）。</summary>
/// <remarks>运行期按租户生效的值是设置 <c>Security.SessionIdleTimeoutDays</c>，它的代码默认值取自这里；消费方读租户策略，不读本选项。</remarks>
public sealed class UserSessionOptions
{
    /// <summary>空闲多久视为会话结束，按整天计。</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromDays(7);
}
#endif
