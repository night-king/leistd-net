#if (RemoteTokenAuth)
namespace CompanyName.ProjectName.Domain.Users.Abstractions;

/// <summary>账户偏好由 Identity 持有，资源服务经这个端口读写；实现在 Infrastructure（缓存与回源）。</summary>
public interface IIdentityAccountPreferences
{
    /// <summary>读取指定租户内指定用户的完整表示。</summary>
    /// <param name="tenantId">租户；宿主为 <see langword="null"/>。</param>
    /// <param name="userId">用户 Id。</param>
    /// <param name="mode">读取方式：当前用户总是向 Identity 重验证，服务端读取可用新鲜期内的缓存。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>暂时读不到（Identity 不可用且没有旧值，或用户在 Identity 不存在）时为 <see langword="null"/>。</returns>
    Task<AccountPreferenceSnapshot?> ReadAsync(
        Guid? tenantId,
        Guid userId,
        AccountPreferenceReadMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>以当前用户本人的委托写入一项偏好，返回 Identity 提交后的完整表示。</summary>
    /// <param name="tenantId">当前租户。</param>
    /// <param name="userId">当前用户；只能是委托令牌代表的那个人。</param>
    /// <param name="field">字段名（<c>language</c>、<c>timeZone</c>、<c>theme</c>）。</param>
    /// <param name="value">新值；<see langword="null"/> 清除本人的覆盖。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AccountPreferenceSnapshot> WriteForCurrentUserAsync(
        Guid? tenantId,
        Guid userId,
        string field,
        string? value,
        CancellationToken cancellationToken = default);
}
#endif
