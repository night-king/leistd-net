namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>登录第二步的邮箱验证码已发出。</summary>
public sealed record TwoFactorEmailCodeOutputDto
{
    /// <summary>验证码的有效期（秒）；第二步本身的有效期更短时以第二步为准。</summary>
    public required int ExpiresInSeconds { get; init; }

    /// <summary>多少秒后才能重发。</summary>
    public required int RetryAfterSeconds { get; init; }
}
