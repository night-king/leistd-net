using System.ComponentModel.DataAnnotations;
using CompanyName.ProjectName.Domain.Users.ValueObjects;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>指定登录第二步的首选方式。</summary>
public sealed record SetPreferredTwoFactorMethodInputDto
{
    /// <summary>身份验证器或邮箱验证码；必须是本人已设置的方式。</summary>
    [Display(Name = "Verification method")]
    [Required(ErrorMessage = "{0} is required.")]
    public TwoFactorMethod? Method { get; init; }
}
