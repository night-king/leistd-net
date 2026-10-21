using Leistd.MultiTenancy.Dtos;
using Leistd.MultiTenancy.Management.Dtos;

namespace Leistd.MultiTenancy.Management;

/// <summary>租户连接的管理面与内部下发。</summary>
/// <remarks>
/// <para>管理面只回名字与版本；明文连接串只经已认证的机器端点，按名字下发给需要连库的服务。</para>
/// <para>连接名可来自外部输入；不合法时抛出带 <c>TenantConnection:NameInvalid</c> 错误码的业务异常。</para>
/// </remarks>
public interface ITenantConnectionManagementService
{
    /// <summary>列出该租户已登记的全部连接（不含连接串）；空列表即不单独分库。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="Exceptions.TenantNotFoundException">租户不存在或已删除。</exception>
    Task<IReadOnlyList<TenantConnectionOutputDto>> GetListAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>按连接名下发运行时连接，供资源服务解析路由。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="name">连接名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="Exceptions.TenantNotFoundException">租户不存在或已删除。</exception>
    Task<TenantRuntimeConnectionOutputDto> GetRuntimeAsync(Guid tenantId, string name, CancellationToken cancellationToken = default);

    /// <summary>按连接名枚举全部登记了连接的租户，供迁移作业使用；取不出连接的租户单列。</summary>
    /// <param name="name">连接名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<TenantMigrationConnectionListOutputDto> GetMigrationListAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>列出某个连接名下的独立库与住在里面的租户，不含连接串，供资源服务的逐库作业使用。</summary>
    /// <param name="name">连接名。</param>
    /// <param name="activeOnly">只列启用租户的库。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<TenantDatabaseListOutputDto> GetDatabaseListAsync(
        string name,
        bool activeOnly,
        CancellationToken cancellationToken = default);

    /// <summary>登记或更新一条连接。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="name">连接名。</param>
    /// <param name="input">连接串与期望版本；先按 DataAnnotations 校验，失败抛 <see cref="System.ComponentModel.DataAnnotations.ValidationException"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<TenantConnectionOutputDto> SetAsync(
        Guid tenantId,
        string name,
        UpsertTenantConnectionInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>删除一条连接，该名字之后回落到服务自己的配置。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="name">连接名。</param>
    /// <param name="expectedVersion">调用方读到的该行版本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RemoveAsync(Guid tenantId, string name, long expectedVersion, CancellationToken cancellationToken = default);
}
