namespace CompanyName.ProjectName.Application.Auth.Captcha;

/// <summary>验证码的缓存键与锁键：签发与校验两侧必须一致。</summary>
internal static class CaptchaKeys
{
    public static string Cache(string token) => $"MyProject:Captcha:{token}";

    public static string Lock(string token) => $"MyProject:Captcha:lock:{token}";
}
