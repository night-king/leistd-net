namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>开始设置两步验证：把密钥添加到身份验证器应用。</summary>
public sealed record TwoFactorSetupOutputDto
{
    /// <summary>这一次设置的 Id，确认时原样带回。</summary>
    public required Guid SetupId { get; init; }

    /// <summary>Base32 密钥，供无法扫码时手动输入。</summary>
    public required string Secret { get; init; }

    /// <summary><c>otpauth://</c> 地址，界面据它生成二维码。</summary>
    public required string OtpAuthUri { get; init; }
}
