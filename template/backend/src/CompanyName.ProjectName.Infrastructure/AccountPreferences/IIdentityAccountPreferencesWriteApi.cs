#if (RemoteTokenAuth)
using System.Text.Json.Nodes;
using Refit;

namespace CompanyName.ProjectName.Infrastructure.AccountPreferences;

/// <summary>以 Token Exchange 代表当前用户本人向 Identity 写入账户偏好。</summary>
public interface IIdentityAccountPreferencesWriteApi
{
    /// <summary>写入一项偏好，成功时返回提交后的完整表示与 ETag。</summary>
    /// <param name="body">只含一个字段的合并补丁。</param>
    /// <param name="acceptLanguage">本次请求的界面语言，让 Identity 按它给出拒绝说明。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [Patch("/api/v1/auth/me/preferences")]
    Task<HttpResponseMessage> PatchAsync(
        [Body] JsonObject body,
        [Header("Accept-Language")] string? acceptLanguage,
        CancellationToken cancellationToken = default);
}
#endif
