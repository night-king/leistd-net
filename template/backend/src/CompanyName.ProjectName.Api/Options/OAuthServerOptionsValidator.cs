#if (OpenIddictServer)
using CompanyName.ProjectName.Api.Auth.Sessions;
using Microsoft.Extensions.Options;

namespace CompanyName.ProjectName.Api.Options;

/// <summary>令牌签发配置的校验：访问令牌寿命在支持范围内；不用开发证书时，签名与加密证书各至少一项、每项给出路径。</summary>
/// <remarks>
/// 宿主组合 OpenIddict 时就要读这些证书（早于启动期校验），组合期复用同一个验证器，不另写一套条件。
/// 文件能否加载、有没有 RSA 私钥、是否重复由宿主加载证书时判断。
/// </remarks>
internal sealed class OAuthServerOptionsValidator : IValidateOptions<OAuthServerOptions>
{
    public ValidateOptionsResult Validate(string? name, OAuthServerOptions options)
    {
        var failures = new List<string>();
        ValidateAccessTokenLifetime(options.AccessTokenLifetime, failures);
        if (!options.UseDevelopmentCertificates)
        {
            ValidateCertificates(options.SigningCertificates, $"{OAuthServerOptions.SectionName}:SigningCertificates", failures);
            ValidateCertificates(options.EncryptionCertificates, $"{OAuthServerOptions.SectionName}:EncryptionCertificates", failures);
        }
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    // 令牌的 exp/iat 以秒计：带小数秒的寿命会被截掉，"长于提前刷新窗口"就可能名存实亡。
    private static void ValidateAccessTokenLifetime(TimeSpan lifetime, List<string> failures)
    {
        if (lifetime.Ticks % TimeSpan.TicksPerSecond != 0 || lifetime <= AccessTokenRenewal.Lead)
            failures.Add($"{OAuthServerOptions.SectionName}:AccessTokenLifetime must be whole seconds and longer than " +
                $"{AccessTokenRenewal.Lead.TotalSeconds:0} seconds (the browser session refresh lead); got {lifetime}.");
    }

    private static void ValidateCertificates(OAuthCertificate[] certificates, string key, List<string> failures)
    {
        if (certificates.Length == 0)
        {
            failures.Add($"{key} requires at least one entry (an RSA certificate distinct from the HTTPS certificate, " +
                $"e.g. {key}:0:Path) unless {OAuthServerOptions.SectionName}:UseDevelopmentCertificates is enabled for local development.");
            return;
        }
        for (var index = 0; index < certificates.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(certificates[index].Path))
                failures.Add($"{key}:{index}:Path is required.");
        }
    }
}
#endif
