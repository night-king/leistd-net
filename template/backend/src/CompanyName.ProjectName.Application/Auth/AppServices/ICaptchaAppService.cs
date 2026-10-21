using CompanyName.ProjectName.Application.Auth.Dtos;
using Leistd.Ddd.Application.Contracts.AppServices;

namespace CompanyName.ProjectName.Application.Auth.AppServices;

public interface ICaptchaAppService : IAppService
{
    /// <summary>生成图形验证码。</summary>
    Task<CaptchaOutputDto> GenerateCaptchaAsync(CancellationToken cancellationToken = default);
}
