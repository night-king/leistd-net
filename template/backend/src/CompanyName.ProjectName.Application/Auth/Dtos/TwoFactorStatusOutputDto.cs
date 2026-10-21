namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>本人的两步验证状态。</summary>
public sealed record TwoFactorStatusOutputDto
{
    /// <summary>是否已启用。</summary>
    public bool Enabled { get; init; }

    /// <summary>剩余可用的恢复码个数。</summary>
    public int RecoveryCodesLeft { get; init; }

    /// <summary>所在租户要求启用两步验证（此时不能停用）。</summary>
    public bool RequiredByPolicy { get; init; }
}
