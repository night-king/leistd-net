#if (LocalIdentity)
using Microsoft.Extensions.Options;

namespace CompanyName.ProjectName.Infrastructure.Auth.Passkeys;

/// <summary>启动期校验通行密钥配置：要么整组不配，要么 RP ID 与每个来源都对得上。</summary>
/// <remarks>配错了到登记时才会被浏览器拒绝，报出来的只是笼统的"操作失败"，所以在启动期指明是哪个键。</remarks>
internal sealed class PasskeyOptionsValidator : IValidateOptions<PasskeyOptions>
{
    public ValidateOptionsResult Validate(string? name, PasskeyOptions options)
    {
        var hasId = !string.IsNullOrWhiteSpace(options.RelyingPartyId);
        var origins = options.ConfiguredOrigins.ToList();
        if (!hasId && origins.Count == 0)
            return ValidateOptionsResult.Success;

        if (!hasId)
            return ValidateOptionsResult.Fail($"{PasskeyOptions.SectionName}:RelyingPartyId is required when {PasskeyOptions.SectionName}:Origins is set.");

        if (origins.Count == 0)
            return ValidateOptionsResult.Fail($"{PasskeyOptions.SectionName}:Origins is required when {PasskeyOptions.SectionName}:RelyingPartyId is set.");

        var rpId = options.RelyingPartyId!.Trim();
        if (rpId.Contains(':') || rpId.Contains('/'))
            return ValidateOptionsResult.Fail($"{PasskeyOptions.SectionName}:RelyingPartyId must be a domain name without scheme or port.");

        var failures = new List<string>();
        foreach (var origin in origins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query))
            {
                failures.Add($"{PasskeyOptions.SectionName}:Origins entry '{origin}' must be an origin such as https://app.example.com.");
                continue;
            }

            // 浏览器只在安全上下文里提供 WebAuthn：localhost 例外，可用 http
            if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
                failures.Add($"{PasskeyOptions.SectionName}:Origins entry '{origin}' must use https.");

            var host = uri.IdnHost;
            if (!host.Equals(rpId, StringComparison.OrdinalIgnoreCase) && !host.EndsWith("." + rpId, StringComparison.OrdinalIgnoreCase))
                failures.Add($"{PasskeyOptions.SectionName}:Origins entry '{origin}' is not under {PasskeyOptions.SectionName}:RelyingPartyId '{rpId}'.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
#endif
