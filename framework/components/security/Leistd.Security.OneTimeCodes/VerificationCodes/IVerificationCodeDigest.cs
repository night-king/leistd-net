namespace Leistd.Security.OneTimeCodes.VerificationCodes;

/// <summary>短期验证码的带密钥摘要与比较；调用方限制有效期和尝试次数。</summary>
public interface IVerificationCodeDigest
{
    /// <summary>计算验证码摘要</summary>
    string Compute(string code);

    /// <summary>恒定时间比对</summary>
    bool Matches(string digest, string code);
}
