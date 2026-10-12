using Leistd.Data.Paging;
using Leistd.MultiTenancy.Exceptions;

namespace Leistd.MultiTenancy.Stores;

/// <summary>管理租户注册表并规范化租户名称。</summary>
/// <remarks>
/// 契约与出参都在 Core：应用层依赖它即可完成租户管理，不必引用任何持久化实现包。
/// EF 实现见 <c>Leistd.MultiTenancy.EntityFrameworkCore</c> 的 <c>AddMultiTenancyEfCore&lt;TDbContext&gt;()</c>。
/// </remarks>
public interface ITenantManager
{
    /// <summary>创建租户，名称在未删除租户中大小写不敏感且唯一。</summary>
    /// <param name="name">租户名称，须匹配 <see cref="TenantConfiguration.NamePattern"/>；原值落库。</param>
    /// <param name="displayName">显示名。</param>
    /// <param name="isActive">初始是否启用。还要初始化角色、管理员等数据时传 <see langword="false"/>，完成后用 <see cref="SetActiveAsync"/> 启用。</param>
    /// <param name="description">简短描述。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="Leistd.ExceptionHandling.BusinessException">
    /// <paramref name="name"/> 不匹配 <see cref="TenantConfiguration.NamePattern"/>（码 <c>Tenant:NameInvalid</c>）。
    /// </exception>
    /// <exception cref="DuplicateTenantNameException">名称已被未删除的租户占用（含并发落败）。</exception>
    Task<TenantConfiguration> CreateAsync(
        string name,
        string? displayName,
        bool isActive,
        string? description = null,
        CancellationToken cancellationToken = default);

    /// <summary>更新租户名称、显示名称与描述。</summary>
    /// <remarks>
    /// 三个字段都按传入值覆盖：<paramref name="displayName"/> 或 <paramref name="description"/> 传 <see langword="null"/> 即清空。
    /// </remarks>
    /// <exception cref="Leistd.ExceptionHandling.BusinessException">
    /// <paramref name="name"/> 与原名不同且不匹配 <see cref="TenantConfiguration.NamePattern"/>（码 <c>Tenant:NameInvalid</c>）；名称不变时不校验。
    /// </exception>
    /// <exception cref="TenantNotFoundException">租户不存在或已删除。</exception>
    /// <exception cref="DuplicateTenantNameException">新名称已被其它未删除租户占用。</exception>
    Task<TenantConfiguration> UpdateAsync(
        Guid id,
        string name,
        string? displayName,
        string? description = null,
        CancellationToken cancellationToken = default);

    /// <summary>更改租户启用状态。</summary>
    /// <remarks>使用直读库的 <c>EfCoreTenantStore</c> 时，停用在提交时对所有节点生效。</remarks>
    /// <exception cref="TenantNotFoundException">租户不存在或已删除。</exception>
    Task<TenantConfiguration> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>软删除租户并保留其业务数据。</summary>
    /// <exception cref="TenantNotFoundException">租户不存在或已删除。</exception>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>按标识查找未删除租户；不存在时返回 <see langword="null"/>。</summary>
    Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>按名称关键字分页查询租户，并按创建时间倒序、同刻按 Id 倒序返回。</summary>
    /// <param name="keyword">名称或显示名关键字；为空不过滤。</param>
    /// <param name="page">分页；排序固定，<see cref="PageRequest.Sorting"/> 不生效。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<PagedResult<TenantConfiguration>> GetPagedAsync(
        string? keyword,
        PageRequest page,
        CancellationToken cancellationToken = default);
}
