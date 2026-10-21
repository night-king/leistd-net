using Leistd.Lock.Abstractions;
using Microsoft.Extensions.Caching.Distributed;

namespace CompanyName.ProjectName.Application.Auth.Captcha;

/// <summary>校验并消费图形验证码，供注册与发送注册验证码共用。</summary>
public class CaptchaVerifier(IDistributedCache distributedCache, IDistributedLock distributedLock) : ICaptchaVerifier
{
    /// <remarks>
    /// 读取、消费与比较必须在同一个临界区里。<c>IDistributedCache</c> 没有原子的
    /// get-and-delete，"读出来再删掉"之间存在窗口：同一个 token 并发提交时两个请求
    /// 都会读到验证码、都判定通过，于是"一次性挑战"只是名义上的一次——
    /// 解一次验证码就能并发提交任意多次注册，正是验证码要挡的那件事。
    /// 临界区用模板已装的分布式锁，不为此另建验证码专用存储或 Redis 脚本。
    /// </remarks>
    public async Task<bool> VerifyAsync(string token, string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(code))
            return false;

        var cacheKey = CaptchaKeys.Cache(token);
        await using var captchaLock = await distributedLock.LockAsync(CaptchaKeys.Lock(token), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            captchaLock.LockLost);
        var operationToken = lockScope.Token;

        var cachedCode = await distributedCache.GetStringAsync(cacheKey, operationToken);

        if (string.IsNullOrEmpty(cachedCode))
            return false;

        // 验证码只能使用一次：无论比对结果如何，token 存在就消费掉——
        // 猜错一次即失效，否则同一个 token 可以被反复试。
        await distributedCache.RemoveAsync(cacheKey, operationToken);

        return cachedCode.Equals(code, StringComparison.OrdinalIgnoreCase);
    }
}
