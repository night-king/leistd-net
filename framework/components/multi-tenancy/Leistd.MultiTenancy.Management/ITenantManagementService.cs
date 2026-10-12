using Leistd.Data.Paging;
using Leistd.MultiTenancy.Management.Dtos;

namespace Leistd.MultiTenancy.Management;

/// <summary>租户管理用例（宿主侧）：查询、创建编排与补偿、更新、启停、删除。</summary>
/// <remarks>
/// <para>创建顺序固定：先以停用态登记租户，并在同一控制面工作单元里登记连接；
/// 再在新租户上下文与新工作单元里经 <see cref="Provisioning.ITenantProvisioner"/> 开通；最后启用。
/// 任一步失败按顺序补偿：清开通数据 → 删连接登记 → 删租户。</para>
/// <para>不做权限检查——由端点策略决定。成功后发布 <see cref="Events.TenantChangedEvent"/>。</para>
/// <para>带入参的用例先按 DataAnnotations 校验（含创建入参里的每条连接），失败抛
/// <see cref="System.ComponentModel.DataAnnotations.ValidationException"/>，此时没有任何写入与事件。</para>
/// </remarks>
public interface ITenantManagementService
{
    /// <summary>按关键字分页查询租户，按创建时间倒序、同刻按 Id 倒序。</summary>
    /// <param name="input">关键字与分页；<see cref="PageRequest.Sorting"/> 不生效。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<PagedResult<TenantOutputDto>> GetPagedAsync(GetTenantPagedInputDto input, CancellationToken cancellationToken = default);

    /// <summary>获取租户详情。</summary>
    /// <param name="id">租户标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="Exceptions.TenantNotFoundException">租户不存在或已删除。</exception>
    Task<TenantOutputDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>创建租户并开通，完成后启用。</summary>
    /// <param name="input">创建入参；宿主可传派生类型携带开通所需的更多信息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<TenantOutputDto> CreateAsync(
        CreateTenantInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>更新名称、显示名与描述。</summary>
    /// <param name="id">租户标识。</param>
    /// <param name="input">新值；全部覆盖。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<TenantOutputDto> UpdateAsync(Guid id, UpdateTenantInputDto input, CancellationToken cancellationToken = default);

    /// <summary>启用或停用；启用前依次经过宿主注册的 <see cref="Provisioning.ITenantActivationGuard"/>。</summary>
    /// <param name="id">租户标识。</param>
    /// <param name="input">目标状态。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<TenantOutputDto> SetActivationAsync(Guid id, UpdateTenantActivationInputDto input, CancellationToken cancellationToken = default);

    /// <summary>软删除租户，业务数据保留但不可达。</summary>
    /// <remarks>租户不存在或已删除（含并发删除先完成）时直接成功，不发布事件；其他失败照常抛出。</remarks>
    /// <param name="id">租户标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

}
