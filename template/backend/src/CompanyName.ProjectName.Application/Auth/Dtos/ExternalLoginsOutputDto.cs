namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>本人的外部账号绑定情况。</summary>
public sealed record ExternalLoginsOutputDto
{
    /// <summary>是否设有密码。没有密码时，最后一个绑定不能解绑（否则就登不进来了）。</summary>
    public bool HasPassword { get; init; }

    /// <summary>部署已配置的提供商，每个附带本人在该提供商下的绑定（未绑定为 null）。</summary>
    public required IReadOnlyList<ExternalLoginProviderOutputDto> Providers { get; init; }
}

/// <summary>一个已配置的外部登录提供商，及本人在其下的绑定。</summary>
public sealed record ExternalLoginProviderOutputDto
{
    /// <summary>提供商标识（<c>github</c>、<c>google</c>）。</summary>
    public required string Provider { get; init; }

    /// <summary>本人的绑定；未绑定为 null。</summary>
    public ExternalLoginLinkOutputDto? Link { get; init; }
}

/// <summary>一个已绑定的外部账号。</summary>
public sealed record ExternalLoginLinkOutputDto
{
    /// <summary>绑定 Id（解绑时用）。</summary>
    public required Guid Id { get; init; }

    /// <summary>外部账号在提供商侧的展示标签（GitHub 的句柄、Google 的邮箱），不是本地用户名。</summary>
    public string? ProviderAccountLabel { get; init; }

    /// <summary>外部账号的邮箱。</summary>
    public string? ProviderEmail { get; init; }

    /// <summary>绑定时间。</summary>
    public DateTime CreationTime { get; init; }
}
