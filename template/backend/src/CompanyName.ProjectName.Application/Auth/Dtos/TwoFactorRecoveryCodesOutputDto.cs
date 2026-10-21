namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>新生成的恢复码（明文只在这一次返回）。</summary>
public sealed record TwoFactorRecoveryCodesOutputDto
{
    /// <summary>恢复码明文。</summary>
    public required IReadOnlyList<string> RecoveryCodes { get; init; }
}
