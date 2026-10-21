using System.Security.Cryptography;
using System.Text;
using CompanyName.ProjectName.Application.Auth.Captcha;
using CompanyName.ProjectName.Application.Auth.Dtos;
using CompanyName.ProjectName.Application.Auth.Policies;
using Leistd.Ddd.Application.AppServices;
using Microsoft.Extensions.Caching.Distributed;

namespace CompanyName.ProjectName.Application.Auth.AppServices;

public class CaptchaAppService(
    IDistributedCache distributedCache,
    IUserRegistrationPolicyProvider registrationPolicy) : BaseAppService, ICaptchaAppService
{
    private const string CaptchaLetters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    private const string CaptchaDigits = "23456789";
    private const string CaptchaCharacters = CaptchaLetters + CaptchaDigits;
    private static readonly string[] BgColors = ["#f0fdf4", "#f8fafc", "#fffbeb", "#fef2f2", "#f0f9ff"];

    public async Task<CaptchaOutputDto> GenerateCaptchaAsync(CancellationToken cancellationToken = default)
    {
        // 注册策略按租户从设置里解析，不再是全进程一份的 IOptions
        var policy = await registrationPolicy.GetAsync(cancellationToken);
        var code = GenerateCode(4);
        var token = Guid.NewGuid().ToString("N");

        var bg = BgColors[RandomNumberGenerator.GetInt32(BgColors.Length)];
        var lineY = RandomNumberGenerator.GetInt32(5, 35);
        var angle = RandomNumberGenerator.GetInt32(-15, 15);

        // viewBox 不能省：没有它，宿主给 <img> 设高度只会裁剪画布而不是等比缩放，
        // 验证码字符会被切掉一截——而这在页面上看起来只是「图有点怪」，不报错。
        var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"130\" height=\"44\" viewBox=\"0 0 130 44\"><rect width=\"100%\" height=\"100%\" fill=\"{bg}\"/><line x1=\"0\" y1=\"{lineY}\" x2=\"130\" y2=\"{44 - lineY}\" stroke=\"#94a3b8\" stroke-width=\"2\" opacity=\"0.6\"/><text x=\"50%\" y=\"50%\" font-size=\"24\" font-family=\"monospace\" fill=\"#0f172a\" font-weight=\"bold\" font-style=\"italic\" textLength=\"88\" lengthAdjust=\"spacingAndGlyphs\" dominant-baseline=\"central\" text-anchor=\"middle\" transform=\"rotate({angle}, 65, 22)\">{code}</text></svg>";

        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
        var imageBase64 = $"data:image/svg+xml;base64,{base64}";

        // 3. 保存到缓存
        var cacheKey = CaptchaKeys.Cache(token);
        var cacheOptions = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(policy.CaptchaExpiryMinutes)
        };
        await distributedCache.SetStringAsync(cacheKey, code, cacheOptions, cancellationToken);

        return new CaptchaOutputDto
        {
            CaptchaToken = token,
            CaptchaImageBase64 = imageBase64
        };
    }

    private static string GenerateCode(int length)
    {
        var chars = new char[length];
        chars[0] = CaptchaLetters[RandomNumberGenerator.GetInt32(CaptchaLetters.Length)];
        chars[1] = CaptchaDigits[RandomNumberGenerator.GetInt32(CaptchaDigits.Length)];

        for (var i = 2; i < chars.Length; i++)
        {
            chars[i] = CaptchaCharacters[RandomNumberGenerator.GetInt32(CaptchaCharacters.Length)];
        }

        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }

}
