using Leistd.Authorization.Constants;

namespace Leistd.Authorization.Grants;

/// <summary>权限授予的只读存储；写入经 <see cref="IPermissionGrantManager"/>。</summary>
/// <remarks>按主体一次取回全部授予，多权限判断在内存完成。</remarks>
public interface IPermissionGrantStore
{
    /// <summary>获取单个授予主体的全部授予及其并发版本。</summary>
    /// <param name="providerName">授予对象类型，见 <see cref="PermissionGrantProviderNames"/>。</param>
    /// <param name="providerKey">授予对象 Key。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该主体的授予集合；没有任何授予时返回空集合且版本为 0。</returns>
    Task<PermissionGrantSet> GetGrantsAsync(
        string providerName,
        string providerKey,
        CancellationToken cancellationToken = default);

    /// <summary>一次取回同类型多个主体的授予及其并发版本；往返次数与主体数量无关。</summary>
    /// <param name="providerName">授予对象类型，见 <see cref="PermissionGrantProviderNames"/>。</param>
    /// <param name="providerKeys">授予对象 Key 集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与 <paramref name="providerKeys"/> 一一对应的授予集合；无授予的主体返回空集合。</returns>
    Task<IReadOnlyList<PermissionGrantSet>> GetGrantsAsync(
        string providerName,
        IReadOnlyCollection<string> providerKeys,
        CancellationToken cancellationToken = default);

    /// <summary>一次取回权限检查主体的全部授予：用户直授加其所属角色的授予。</summary>
    /// <param name="userId">用户 ID。</param>
    /// <param name="roleIds">用户所属角色 ID 集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<SubjectPermissionGrants> GetGrantsForSubjectAsync(
        string userId,
        IReadOnlyCollection<string> roleIds,
        CancellationToken cancellationToken = default);
}
