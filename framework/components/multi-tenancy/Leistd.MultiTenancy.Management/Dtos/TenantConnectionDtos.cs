using System.ComponentModel.DataAnnotations;
using Leistd.MultiTenancy.ConnectionStrings;

namespace Leistd.MultiTenancy.Management.Dtos;

/// <summary>租户的一条连接登记（管理面）。</summary>
/// <remarks>连接串只写不读：管理面只回名字与版本。租户一条登记都没有，就表示它不单独分库。</remarks>
public sealed record TenantConnectionOutputDto
{
    /// <summary>租户标识。</summary>
    public required Guid TenantId { get; init; }

    /// <summary>归一化后的连接名。</summary>
    public required string Name { get; init; }

    /// <summary>该行的乐观并发版本。</summary>
    public required long Version { get; init; }
}

/// <summary>登记或更新一条租户连接；连接名走路由。</summary>
/// <remarks>想让某个名字回到"用服务自己的库"，删掉这一条，而不是提交空连接串。</remarks>
public sealed record UpsertTenantConnectionInputDto
{
    /// <summary>调用方读到的该行版本；<see langword="null"/> 表示预期这一行尚不存在（首次登记）。</summary>
    /// <remarks>
    /// 字段必须出现、值可以是 <see langword="null"/>：遗漏不能被当成首次登记而按后写者胜出处理。
    /// 声明为 <c>required</c> 约束的是 JSON 里有没有这个键，缺键在反序列化阶段即被拒绝（400，没有字段级错误），
    /// 而 <c>[Required]</c> 会连显式 <see langword="null"/> 一并拒掉，因此不用它。
    /// </remarks>
    public required long? ExpectedVersion { get; init; }

    /// <summary>连接串（明文）；加密后存入控制库，运行与迁移共用。</summary>
    [Required]
    [MaxLength(TenantConnectionConfiguration.MaxConnectionStringLength)]
    public string ConnectionString { get; init; } = null!;

    /// <summary>不输出连接串。</summary>
    public override string ToString() =>
        $"{nameof(UpsertTenantConnectionInputDto)} {{ ExpectedVersion = {ExpectedVersion} }}";
}
