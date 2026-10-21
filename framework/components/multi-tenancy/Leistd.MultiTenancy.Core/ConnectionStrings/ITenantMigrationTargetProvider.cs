namespace Leistd.MultiTenancy.ConnectionStrings;

/// <summary>枚举租户登记的独立物理库，每个库一条。</summary>
/// <remarks>
/// <para>结果带明文连接串，直接调用仅限迁移作业（DbMigrator 一类的一次性进程）；请求入口与后台作业改用
/// 只回指纹与租户归属的 <see cref="ITenantDatabaseEnumerator"/>。</para>
/// <para>连接名解析口径与运行时相同（精确名 → 默认名 → 登记过却都不命中即失败）。取不出连接的租户列进
/// <see cref="TenantMigrationTargetSet.FailedTenants"/>：迁移作业须迁完其余库后报出它们并以失败结束。</para>
/// </remarks>
public interface ITenantMigrationTargetProvider
{
    /// <summary>读取全部登记了连接的租户在指定连接名下的物理库，同一连接串只出现一次。</summary>
    /// <remarks>
    /// 多个租户共用一个库时，<see cref="TenantMigrationTarget.TenantId"/> 取其中标识最小者，结果稳定；
    /// 库的顺序按首次出现。去重按连接串全文比较，写法不同但指向同一个库的连接串会各出现一次，
    /// 因此逐库逻辑（迁移本身即是）须能重复执行。
    /// </remarks>
    /// <param name="name">连接名，通常是业务 DbContext 的 <c>[ConnectionStringName]</c>；大小写不敏感。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<TenantMigrationTargetSet> GetDedicatedTargetsAsync(
        string name,
        CancellationToken cancellationToken = default);
}

/// <summary>迁移目标清单，与运行时的 <see cref="TenantDatabaseSet"/> 同形。</summary>
/// <param name="Targets">按物理库去重后的目标。</param>
/// <param name="FailedTenants">取不出连接的租户；它们不在 <paramref name="Targets"/> 里。</param>
public sealed record TenantMigrationTargetSet(
    IReadOnlyList<TenantMigrationTarget> Targets,
    IReadOnlyList<TenantDatabaseFailure> FailedTenants);

/// <summary>一个独立物理库目标。</summary>
/// <param name="TenantId">进入该库时切换到的代表租户；多个租户共用该库时取标识最小者。</param>
/// <param name="ConnectionString">明文连接串；<see cref="ToString"/> 不输出它。</param>
public sealed record TenantMigrationTarget(Guid TenantId, string ConnectionString)
{
    /// <summary>连接串的 SHA-256 指纹（十六进制），用于日志与去重，不暴露连接串本身。</summary>
    public string Fingerprint => TenantDatabaseFingerprint.Of(ConnectionString);

    /// <inheritdoc />
    public override string ToString() => $"{nameof(TenantMigrationTarget)} {{ TenantId = {TenantId}, Fingerprint = {Fingerprint} }}";
}
