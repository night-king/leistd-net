using Leistd.MultiTenancy.Exceptions;

namespace Leistd.MultiTenancy.ConnectionStrings;

/// <summary>租户连接的唯一写入口，负责名字归一化、连接串加密与逐行的版本不变量。</summary>
/// <remarks>
/// 连接串在写入时加密存储，库、备份与只读账号里都只有密文；实现不得在日志或异常消息里带出连接串。
/// </remarks>
public interface ITenantConnectionConfigurationManager
{
    /// <summary>登记或更新租户在某个连接名下的连接。</summary>
    /// <param name="tenantId">目标租户。</param>
    /// <param name="name">连接名，通常是使用方 DbContext 的 <c>[ConnectionStringName]</c>；归一化为小写后须匹配 <see cref="TenantConnectionConfiguration.NamePattern"/>。</param>
    /// <param name="connectionString">明文连接串，运行时与迁移共用，最长 <see cref="TenantConnectionConfiguration.MaxConnectionStringLength"/> 个字符。</param>
    /// <param name="expectedVersion">
    /// 调用方读到的该行版本；<see langword="null"/> 表示预期这一行尚不存在，不表示跳过并发校验。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="TenantNotFoundException">租户不存在或已删除。</exception>
    /// <exception cref="ExceptionHandling.BusinessException">
    /// 名字不合法（<c>TenantConnection:NameInvalid</c>），或连接串为空、超长、不是键值对语法（<c>TenantConnection:ConnectionStringInvalid</c>）。
    /// </exception>
    /// <exception cref="TenantConnectionVersionConflictException">
    /// <paramref name="expectedVersion"/> 与实际不符，含"预期存在却不存在"与"预期不存在却已存在"两种。
    /// </exception>
    /// <exception cref="TenantConnectionChangeRequiresInactiveTenantException">修改已有行时租户仍处于启用状态。</exception>
    Task<TenantConnectionConfiguration> SetAsync(
        Guid tenantId,
        string name,
        string connectionString,
        long? expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>删除租户在某个连接名下的连接。</summary>
    /// <remarks>删除后该名字回落到服务自己的配置。与修改相同，删除前必须先停用租户。</remarks>
    /// <param name="tenantId">目标租户。</param>
    /// <param name="name">连接名，大小写不敏感。</param>
    /// <param name="expectedVersion">调用方读到的该行版本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="TenantNotFoundException">租户不存在或已删除。</exception>
    /// <exception cref="ExceptionHandling.BusinessException">名字不合法（<c>TenantConnection:NameInvalid</c>）。</exception>
    /// <exception cref="TenantConnectionVersionConflictException">该行不存在，或版本与实际不符。</exception>
    /// <exception cref="TenantConnectionChangeRequiresInactiveTenantException">租户仍处于启用状态。</exception>
    Task RemoveAsync(
        Guid tenantId,
        string name,
        long expectedVersion,
        CancellationToken cancellationToken = default);
}
