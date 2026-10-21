using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>确认这一次设置：用新验证器上的验证码启用或完成换绑。</summary>
public sealed record EnableTwoFactorInputDto
{
    /// <summary>开始设置时返回的设置 Id：只认这一次设置，别处重新开始过就作废。</summary>
    [Display(Name = "Setup id")]
    [Required(ErrorMessage = "{0} is required.")]
    public Guid? SetupId { get; init; }

    /// <summary>新身份验证器上的 6 位验证码。</summary>
    [Display(Name = "Verification code")]
    [Required(ErrorMessage = "{0} is required.")]
    [RegularExpression(@"^\s*\d{3}\s?\d{3}\s*$", ErrorMessage = "The verification code must contain exactly 6 digits.")]
    public string Code { get; init; } = null!;
}
