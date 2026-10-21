namespace Leistd.MultiTenancy.ConnectionStrings;

/// <summary>租户在某个连接名下登记的连接配置快照。</summary>
/// <remarks>
/// <para>连接名对应使用方 DbContext 的 <c>[ConnectionStringName]</c>，一个租户可按服务各登记一条；
/// 一条都没有即该租户不单独分库，各服务使用自己配置的数据库。</para>
/// <para><see cref="ConnectionString"/> 是已解密的明文，只在进程内流转：<see cref="ToString"/> 不输出它，
/// 调用方不得把它写进日志、异常消息或响应。</para>
/// </remarks>
public sealed class TenantConnectionConfiguration
{
    /// <summary>连接串的最大长度（明文字符数）。</summary>
    public const int MaxConnectionStringLength = 2048;

    /// <summary>连接名的最大长度。</summary>
    public const int MaxNameLength = 64;

    /// <summary>连接名的合法形态：小写字母、数字与连字符。输入先归一化为小写，<c>Crm</c> 与 <c>crm</c> 命中同一行。</summary>
    public const string NamePattern = "^[a-z0-9-]{1,64}$";

    /// <summary>租户 Id。</summary>
    public required Guid TenantId { get; init; }

    /// <summary>归一化后的连接名。</summary>
    public required string Name { get; init; }

    /// <summary>连接串（明文）；运行时与迁移共用这一条。</summary>
    public required string ConnectionString { get; init; }

    /// <summary>从 1 开始、每次修改递增的配置版本；每一行独立计数。</summary>
    public long Version { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        $"{nameof(TenantConnectionConfiguration)} {{ TenantId = {TenantId}, Name = {Name}, Version = {Version} }}";
}
