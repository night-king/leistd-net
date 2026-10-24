namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>登记结果：新登记的通行密钥；它是第一个独立因子时一并给出新发的恢复码。</summary>
public sealed record PasskeyRegistrationOutputDto
{
    /// <summary>新登记的通行密钥。</summary>
    public PasskeyOutputDto Passkey { get; init; } = null!;

    /// <summary>新发的恢复码明文，只返回这一次；已有恢复码时为空。</summary>
    public IReadOnlyList<string> RecoveryCodes { get; init; } = [];
}
