namespace Leistd.MultiTenancy.ConnectionStrings;

/// <summary>列出逐库作业要走的全部库：宿主库，加上该连接名下的独立库。</summary>
/// <remarks>
/// <para>清单不含连接串：拿到 <see cref="TenantDatabase.TenantId"/> 切进租户上下文后，连接由正常解析链给出。</para>
/// <para>没有注册任何租户连接解析时，所有租户都在宿主库里，清单就只有宿主库。</para>
/// </remarks>
public interface ITenantDatabaseEnumerator
{
    /// <summary>列出宿主库与独立库。</summary>
    /// <param name="name">连接名（使用方 DbContext 的 <c>[ConnectionStringName]</c>）。</param>
    /// <param name="activeOnly">
    /// 只列启用租户的库。必须显式给出：保留期一类作业要连停用租户的数据一起处理，
    /// 刷新进程内状态一类作业不该连可能已下线的库。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<TenantDatabaseSet> GetDatabasesAsync(
        string name,
        bool activeOnly,
        CancellationToken cancellationToken = default);
}

/// <summary>逐库作业的清单。</summary>
/// <param name="Databases">宿主库在前，独立库在后。</param>
/// <param name="FailedTenants">解析不出连接的租户；它们不在 <paramref name="Databases"/> 里。</param>
public sealed record TenantDatabaseSet(
    IReadOnlyList<TenantDatabase> Databases,
    IReadOnlyList<TenantDatabaseFailure> FailedTenants);

/// <summary>一个物理库。</summary>
/// <param name="TenantId">进这个库用的租户；<see langword="null"/> 表示用宿主配置进。</param>
/// <param name="Fingerprint">
/// 连接配置的指纹，用于判定“是不是同一个库”；宿主库与独立库用同一算法，相同连接只列一次。
/// 空串表示算不出来（单库模式下只有宿主库一项），不参与相等比较。
/// </param>
/// <param name="TenantIds">
/// 住在这个库里的全部租户，升序；宿主库为空。
/// </param>
public sealed record TenantDatabase(Guid? TenantId, string Fingerprint, IReadOnlyList<Guid> TenantIds)
{
    /// <summary>宿主库：用宿主配置进，指纹由宿主连接算出。</summary>
    /// <param name="fingerprint">宿主连接的指纹。</param>
    public static TenantDatabase ForHost(string fingerprint) => new(null, fingerprint, []);
}
