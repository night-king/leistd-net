#if (RemoteTokenAuth)
namespace CompanyName.ProjectName.Infrastructure.AccountPreferences;

/// <summary>资源服务读取 Identity 账户偏好的缓存时长，绑定 <c>Leistd:ServiceClients:Identity:AccountPreferences</c>。</summary>
public sealed class AccountPreferencesClientOptions
{
    /// <summary>配置节。</summary>
    public const string SectionName = "Leistd:ServiceClients:Identity:AccountPreferences";

    /// <summary>服务端读取（收件人文本、按时区统计）在这段时间内直接用缓存，不回源。当前用户的 <c>/me</c> 不受新鲜期限制，故障节流期外每次都条件重验证。</summary>
    public TimeSpan FreshFor { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>上一次成功取得的表示保留多久：期间 Identity 不可用时用它，过期后没有可用的旧值。须大于 <see cref="FreshFor"/>。</summary>
    public TimeSpan RetainFor { get; set; } = TimeSpan.FromHours(24);
}
#endif
