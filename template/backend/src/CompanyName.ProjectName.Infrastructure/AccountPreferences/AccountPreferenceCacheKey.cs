#if (RemoteTokenAuth)
namespace CompanyName.ProjectName.Infrastructure.AccountPreferences;

/// <summary>缓存键：租户（宿主为 <see langword="null"/>）与用户。</summary>
/// <param name="TenantId">租户 Id。</param>
/// <param name="UserId">用户 Id。</param>
internal readonly record struct AccountPreferenceCacheKey(Guid? TenantId, Guid UserId)
{
    /// <summary>Identity 机器端点路径里的租户段。</summary>
    public string TenantSegment => TenantId?.ToString() ?? "host";
}
#endif
