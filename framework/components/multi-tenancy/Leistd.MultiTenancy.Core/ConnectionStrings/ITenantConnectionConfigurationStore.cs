namespace Leistd.MultiTenancy.ConnectionStrings;

/// <summary>按连接名读取租户连接。</summary>
/// <remarks>
/// <para>同一个契约表达两种宿主形态，按注册方式二选一：</para>
/// <list type="bullet">
/// <item>自己持有控制库的服务用 EF 实现（<c>AddMultiTenancyEfCore</c>），直接读库并解密连接串；</item>
/// <item>资源服务用远端实现（<c>Leistd.MultiTenancy.ServiceClient</c> 的 <c>AddRemoteTenantConnectionStore</c>），
/// 回源控制面经 <c>MapTenantConnections</c> 暴露的机器端点。端点与客户端共用 Core 里的线上 DTO，
/// 鉴权方式与弹性策略由宿主在返回的 <c>IHttpClientBuilder</c> 上决定。
/// 控制面下发已解密的连接串，本服务不持有控制面的密钥环。</item>
/// </list>
/// <para>一次只按名字返回一条，不把租户在其他服务的连接一并下发；“精确名 → 默认名”的回落由实现完成。</para>
/// <para>两种实现不能同时注册：谁是权威由注册方式决定，<c>AddMultiTenancyEfCore</c> 在注册期断言这一点。</para>
/// <para>远端实现只在远端解析的共享任务里、于该任务自己的服务作用域内被解析，可以注册为 Scoped，
/// 但不能依赖发起请求的作用域（如 <c>HttpContext</c>）。调用失败必须抛异常；
/// 返回的 <see cref="TenantConnectionLookupResult.TenantId"/> 会与请求的租户比对，
/// 不一致时解析失败，不会被缓存或使用。实现不得记录响应里的连接串。</para>
/// </remarks>
public interface ITenantConnectionConfigurationStore
{
    /// <summary>读取租户在指定连接名下的连接。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="name">连接名，通常是使用方 DbContext 的 <c>[ConnectionStringName]</c>；大小写不敏感。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// <see langword="null"/> 表示租户不存在或已删除，调用方必须失败关闭；租户存在时一定非空，
    /// 由 <see cref="TenantConnectionLookupResult.HasAnyConnection"/> 区分“不分库”与“缺这个名字”。
    /// </returns>
    Task<TenantConnectionLookupResult?> FindAsync(
        Guid tenantId,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>枚举所有登记了连接的未删除租户在该名字下解析出的连接，供 DbMigrator 计算迁移目标。</summary>
    /// <remarks>
    /// <para>一条连接都没有的租户不出现——它们跟着宿主自己的库迁移。</para>
    /// <para>登记过连接却取不出这个名字下连接串的租户（名字解析不出、密文解不开）列进
    /// <see cref="TenantMigrationConnectionListResult.FailedTenants"/>，其余租户照常返回。
    /// 调用方须把它们报出来并以失败结束，不能当作“没有目标”略过，否则那些库停在旧结构上。</para>
    /// <para>控制库读不出、远端回源失败、取消等不属于某个租户的错误照常抛出。</para>
    /// </remarks>
    /// <param name="name">连接名，大小写不敏感。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<TenantMigrationConnectionListResult> GetListAsync(
        string name,
        CancellationToken cancellationToken = default);
}

/// <summary>迁移作业的连接清单，与运行时的 <see cref="TenantDatabaseListResult"/> 同一种失败表达。</summary>
/// <param name="Connections">解析出的连接，每个租户一条。</param>
/// <param name="FailedTenants">取不出连接的租户；它们不在 <paramref name="Connections"/> 里。</param>
public sealed record TenantMigrationConnectionListResult(
    IReadOnlyList<TenantMigrationConnection> Connections,
    IReadOnlyList<TenantDatabaseFailure> FailedTenants);

/// <summary>某个租户在某个连接名下解析出的连接。</summary>
/// <param name="TenantId">租户标识。</param>
/// <param name="Name">实际命中的连接名（精确名或默认名回落），用于诊断迁移落点。</param>
/// <param name="ConnectionString">明文连接串；<see cref="ToString"/> 不输出它。</param>
public sealed record TenantMigrationConnection(Guid TenantId, string Name, string ConnectionString)
{
    /// <inheritdoc />
    public override string ToString() =>
        $"{nameof(TenantMigrationConnection)} {{ TenantId = {TenantId}, Name = {Name} }}";
}
