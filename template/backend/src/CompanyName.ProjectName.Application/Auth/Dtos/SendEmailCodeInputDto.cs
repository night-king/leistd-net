using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

public record SendEmailCodeInputDto
{
    [Display(Name = "Email")]
    [Required(ErrorMessage = "{0} is required.")]
    [EmailAddress(ErrorMessage = "{0} has an invalid format.")]
    public string Email { get; init; } = null!;

    [Display(Name = "Captcha token")]
    [Required(ErrorMessage = "The captcha has expired. Refresh it and try again.")]
    public string CaptchaToken { get; init; } = null!;

    [Display(Name = "Captcha")]
    [Required(ErrorMessage = "{0} is required.")]
    public string CaptchaCode { get; init; } = null!;
}
