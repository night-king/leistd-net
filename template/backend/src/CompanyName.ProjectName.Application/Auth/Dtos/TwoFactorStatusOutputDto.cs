using CompanyName.ProjectName.Domain.Users.ValueObjects;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>本人的两步验证状态。</summary>
public sealed record TwoFactorStatusOutputDto
{
    /// <summary>是否已启用身份验证器。</summary>
    public bool Enabled { get; init; }

    /// <summary>剩余可用的恢复码个数。</summary>
    public int RecoveryCodesLeft { get; init; }

    /// <summary>所在租户要求启用两步验证（此时不能停用身份验证器；邮箱验证码不算满足）。</summary>
    public bool RequiredByPolicy { get; init; }

    /// <summary>本人指定的首选方式；未指定为 null。</summary>
    public TwoFactorMethod? PreferredMethod { get; init; }

    /// <summary>登录第二步此刻直接给出的方式；不需要第二步时为 null。</summary>
    public TwoFactorMethod? DefaultMethod { get; init; }
#if (Email)

    /// <summary>本人开启了邮箱验证码。</summary>
    public bool EmailEnabled { get; init; }

    /// <summary>邮箱验证码此刻为什么用不了（开启前也按它判断能否开启）；能用时为 null。</summary>
    public TwoFactorEmailUnavailableReason? EmailUnavailableReason { get; init; }

    /// <summary>收码地址的打码形式。</summary>
    public string? MaskedEmail { get; init; }
#endif
}
