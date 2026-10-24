namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>本人登记的一把通行密钥。</summary>
public sealed record PasskeyOutputDto
{
    public Guid Id { get; init; }

    /// <summary>名称。</summary>
    public string Name { get; init; } = null!;

    /// <summary>登记时刻（UTC）。</summary>
    public DateTime CreationTime { get; init; }

    /// <summary>最近一次使用（UTC）；从未使用为 null。</summary>
    public DateTime? LastUsedTime { get; init; }

    /// <summary>凭据可以被同步备份（多设备通行密钥）。</summary>
    public bool IsBackupEligible { get; init; }

    /// <summary>凭据当前已备份。</summary>
    public bool IsBackedUp { get; init; }
}
