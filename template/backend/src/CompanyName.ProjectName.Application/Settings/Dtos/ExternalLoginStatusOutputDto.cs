#if (ExternalLogin)
namespace CompanyName.ProjectName.Application.Settings.Dtos;

/// <summary>外部登录各提供方在本实例上的生效状态，供系统设置页说明开关为什么没有生效、回调地址填什么。</summary>
/// <remarks>机密设置只写不回显，密钥来自部署配置时设置页本身看不出已配置，因此由这里给出布尔值；任何字段都不含凭据内容。</remarks>
public sealed record ExternalLoginStatusOutputDto
{
    /// <summary>按提供方标识排序。</summary>
    public required IReadOnlyList<ExternalLoginProviderStatusOutputDto> Providers { get; init; }
}

/// <summary>一个提供方的生效状态。</summary>
public sealed record ExternalLoginProviderStatusOutputDto
{
    /// <summary>提供方标识（<c>github</c>、<c>google</c>）。</summary>
    public required string Provider { get; init; }

    /// <summary>开关是否打开（启用意图）。</summary>
    public bool Enabled { get; init; }

    /// <summary>是否配置了 Client ID（设置或部署配置）。</summary>
    public bool HasClientId { get; init; }

    /// <summary>是否配置了 Client Secret（设置或部署配置）。</summary>
    public bool HasClientSecret { get; init; }

    /// <summary>本实例当前是否提供该入口。多实例在刷新周期内可能各不相同。</summary>
    public bool Active { get; init; }

    /// <summary>要登记到提供方的回调地址，按本次请求的地址拼出，与发起登录时的 redirect_uri 一致。</summary>
    public required string CallbackUrl { get; init; }
}
#endif
