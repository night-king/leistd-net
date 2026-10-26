#if (RemoteTokenAuth)
using Refit;

namespace CompanyName.ProjectName.Infrastructure.AccountPreferences;

/// <summary>以本服务的机器身份向 Identity 读取账户偏好（机器 scope <c>account-preferences.read</c>）。</summary>
/// <remarks>返回原始响应：要按 ETag 处理 304，并区分上游故障与认证拒绝。</remarks>
public interface IIdentityAccountPreferencesReadApi
{
    /// <summary>读取指定租户内指定用户的完整表示。</summary>
    /// <param name="tenant"><c>host</c> 或租户 Id。</param>
    /// <param name="userId">用户 Id。</param>
    /// <param name="ifNoneMatch">上一次的 ETag；没有时不带。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [Get("/api/v1/internal/account-preferences/{tenant}/{userId}")]
    Task<HttpResponseMessage> GetAsync(
        string tenant,
        Guid userId,
        [Header("If-None-Match")] string? ifNoneMatch,
        CancellationToken cancellationToken = default);
}
#endif
