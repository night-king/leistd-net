namespace Leistd.MultiTenancy.Stores;

/// <summary>提供租户校验和登录前探测所需的配置。</summary>
/// <remarks>
/// 框架不默认注册实现：持有租户注册表的服务使用 <c>Leistd.MultiTenancy.EntityFrameworkCore</c> 的 EF 实现。
/// 只消费令牌租户 claim 的资源服务不需要本契约，把 <c>MultiTenancyOptions.ValidateResolvedTenant</c>
/// 置为 <see langword="false"/> 即可。
/// </remarks>
public interface ITenantStore
{
    /// <summary>按标识查找租户；不存在时返回 <see langword="null"/>。</summary>
    Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>按归一化名称查找租户；不存在时返回 <see langword="null"/>。</summary>
    Task<TenantConfiguration?> FindByNameAsync(string normalizedName, CancellationToken cancellationToken = default);
}
