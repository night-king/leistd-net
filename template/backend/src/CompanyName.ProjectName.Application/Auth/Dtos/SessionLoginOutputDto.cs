using CompanyName.ProjectName.Domain.Users.ValueObjects;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>登录第一步（密码或外部登录）的结果。</summary>
public sealed record SessionLoginOutputDto
{
    /// <summary>还需要第二步：凭 <see cref="TwoFactorToken"/> 以其中一种方式通过后才会下发会话。</summary>
    public bool RequiresTwoFactor { get; init; }

    /// <summary>外部登录开始时校验、随受保护票据恢复的站内回跳地址。</summary>
    public string? ReturnUrl { get; init; }

    /// <summary>第二步凭据，几分钟内有效；不需要第二步时为 null。</summary>
    public string? TwoFactorToken { get; init; }

    /// <summary>第二步能用的方式，按系统顺序，恢复码在最后；不需要第二步时为 null。</summary>
    public IReadOnlyList<TwoFactorMethod>? TwoFactorMethods { get; init; }

    /// <summary>第二步直接给出的方式（本人首选，否则按系统顺序）；不需要第二步时为 null。</summary>
    public TwoFactorMethod? DefaultTwoFactorMethod { get; init; }

    /// <summary>邮箱验证码能用时，收码地址的打码形式（如 <c>j***@example.com</c>）。</summary>
    public string? MaskedEmail { get; init; }
}
