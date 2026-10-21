using Leistd.MultiTenancy.ConnectionStrings;

namespace Leistd.MultiTenancy.Dtos;

/// <summary>下发给资源服务的运行时查询结果，形态与 <see cref="TenantConnectionLookupResult"/> 一致。</summary>
/// <remarks>控制面端点与远端连接存储共用这一份线上契约。</remarks>
public sealed record TenantRuntimeConnectionOutputDto
{
    /// <summary>被查询的租户。</summary>
    public required Guid TenantId { get; init; }

    /// <summary>该租户是否登记过任意连接；<see langword="false"/> 表示不分库，调用方用自己的配置。</summary>
    public required bool HasAnyConnection { get; init; }

    /// <summary>命中的连接；登记过却为空表示缺这个名字，调用方必须失败关闭。</summary>
    public TenantConnectionDetailOutputDto? Connection { get; init; }

    /// <summary>不输出连接串。</summary>
    public override string ToString() =>
        $"{nameof(TenantRuntimeConnectionOutputDto)} {{ TenantId = {TenantId}, " +
        $"HasAnyConnection = {HasAnyConnection}, Name = {Connection?.Name ?? "<none>"} }}";
}

/// <summary>命中的那一条连接，<see cref="ConnectionString"/> 为解密后的明文。</summary>
public sealed record TenantConnectionDetailOutputDto
{
    /// <summary>实际命中的连接名（精确名或默认名）。</summary>
    public required string Name { get; init; }

    /// <summary>连接串明文；不写进日志。</summary>
    public required string ConnectionString { get; init; }

    /// <summary>该行的版本。</summary>
    public required long Version { get; init; }

    /// <summary>不输出连接串（记录类型默认打印全部属性）。</summary>
    public override string ToString() =>
        $"{nameof(TenantConnectionDetailOutputDto)} {{ Name = {Name}, Version = {Version} }}";
}

/// <summary>下发给迁移作业的一条连接，<see cref="ConnectionString"/> 为解密后的明文。</summary>
public sealed record TenantMigrationConnectionOutputDto
{
    /// <summary>租户标识。</summary>
    public required Guid TenantId { get; init; }

    /// <summary>实际命中的连接名，用于回答"为什么迁到了这个库"。</summary>
    public required string Name { get; init; }

    /// <summary>连接串明文；不写进日志。</summary>
    public required string ConnectionString { get; init; }

    /// <summary>不输出连接串。</summary>
    public override string ToString() =>
        $"{nameof(TenantMigrationConnectionOutputDto)} {{ TenantId = {TenantId}, Name = {Name} }}";
}

/// <summary>迁移作业的连接清单，与 <see cref="TenantDatabaseListOutputDto"/> 同一种失败表达。</summary>
/// <remarks>
/// 取不出连接的租户与清单一起下发：迁移作业迁完其余库后把它们报出来并以失败结束，不能当作"没有目标"。
/// </remarks>
public sealed record TenantMigrationConnectionListOutputDto
{
    /// <summary>解析出的连接，每个租户一条。</summary>
    public required IReadOnlyList<TenantMigrationConnectionOutputDto> Connections { get; init; }

    /// <summary>取不出连接的租户。</summary>
    public required IReadOnlyList<TenantDatabaseFailureOutputDto> FailedTenants { get; init; }
}

/// <summary>一个独立库，以及住在里面的租户。</summary>
/// <remarks>不含连接串：连接由各租户的正常解析链取得，因此只需读路由权限。</remarks>
public sealed record TenantDatabaseOutputDto
{
    /// <summary>连接串指纹，用于判定"是不是同一个库"；不可逆推连接串。</summary>
    public required string Fingerprint { get; init; }

    /// <summary>住在这个库里的租户。</summary>
    public required IReadOnlyList<Guid> TenantIds { get; init; }
}

/// <summary>一个解析不出连接的租户。</summary>
/// <remarks>与清单一起下发，不阻止其余库的作业或迁移，由调用方报出。</remarks>
public sealed record TenantDatabaseFailureOutputDto
{
    /// <summary>租户标识。</summary>
    public required Guid TenantId { get; init; }

    /// <summary>诊断消息；不含连接串。</summary>
    public required string Reason { get; init; }
}

/// <summary>逐库作业的库清单。</summary>
public sealed record TenantDatabaseListOutputDto
{
    /// <summary>独立库。</summary>
    public required IReadOnlyList<TenantDatabaseOutputDto> Databases { get; init; }

    /// <summary>解析不出连接、本轮被跳过的租户。</summary>
    public required IReadOnlyList<TenantDatabaseFailureOutputDto> FailedTenants { get; init; }
}
