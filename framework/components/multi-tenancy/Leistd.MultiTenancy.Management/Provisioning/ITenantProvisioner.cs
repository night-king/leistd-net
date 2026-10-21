using Leistd.MultiTenancy.Stores;
using Leistd.MultiTenancy.Management.Dtos;

namespace Leistd.MultiTenancy.Management.Provisioning;

/// <summary>租户开通：创建租户时在新租户里写入初始数据（角色、权限、管理员），失败时清掉写过的东西。由宿主实现。</summary>
/// <remarks>
/// <para>两个方法都在目标租户上下文与各自新开的工作单元里调用，写入归属该租户，分库租户的写入落进它的专属库。</para>
/// <para><see cref="ProvisionAsync"/> 抛出即触发补偿：先 <see cref="PurgeAsync"/>，再删连接登记，最后删注册表记录。
/// <see cref="PurgeAsync"/> 必须幂等——它可能面对只开通了一半的租户。</para>
/// <para>宿主要收集更多开通信息（如管理员邮箱与口令）时，派生 <see cref="CreateTenantInputDto"/>，
/// 端点按派生类型绑定请求体，这里从 <see cref="TenantProvisioningContext.Input"/> 取回。</para>
/// </remarks>
public interface ITenantProvisioner
{
    /// <summary>在新租户里写入初始数据。</summary>
    /// <param name="context">新租户与创建入参。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ProvisionAsync(TenantProvisioningContext context, CancellationToken cancellationToken = default);

    /// <summary>清除开通写入的数据，用于创建失败后的补偿；幂等。</summary>
    /// <param name="context">待回滚的租户与创建入参。</param>
    /// <param name="cancellationToken">取消令牌；补偿使用独立令牌，不随调用方取消而中止。</param>
    Task PurgeAsync(TenantProvisioningContext context, CancellationToken cancellationToken = default);
}

/// <summary>一次开通的上下文。</summary>
/// <param name="Tenant">新建（尚未启用）的租户。</param>
/// <param name="Input">创建入参；宿主派生类型时即为派生实例。</param>
public sealed record TenantProvisioningContext(TenantConfiguration Tenant, CreateTenantInputDto Input);
