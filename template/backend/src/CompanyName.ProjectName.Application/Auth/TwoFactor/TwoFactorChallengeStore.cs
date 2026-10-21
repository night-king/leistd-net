#if (LocalIdentity)
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Leistd.Timing;
using Microsoft.Extensions.Caching.Distributed;
using System.Text;

namespace CompanyName.ProjectName.Application.Auth.TwoFactor;

/// <summary>登录第二步的挑战：密码（或外部登录）已通过、尚待验证码的那一段。</summary>
/// <remarks>
/// <para>凭据放在缓存里、只把随机令牌交给浏览器：这一段还不是会话，不能发 Cookie——
/// 发了就等于第一步即登录成功，第二步形同虚设。</para>
/// <para>同一挑战最多试 <see cref="MaxAttempts"/> 次，用完须重新输入密码；
/// 每次输错同时计入账号的登录失败次数，所以换挑战重试也绕不过锁定。</para>
/// <para>有效期从签发那一刻算起，输错不会延长：到期时刻记在挑战里，重写只沿用剩余时长。
/// 到期以注入的时钟为准，缓存条目的过期只负责回收。</para>
/// </remarks>
internal sealed class TwoFactorChallengeStore(IDistributedCache cache, IClock clock)
{
    /// <summary>挑战的有效期。</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>单个挑战允许的错误次数。</summary>
    public const int MaxAttempts = 5;

    private const string KeyPrefix = "auth:2fa-login:";

    /// <param name="userId">第一步认出的用户。</param>
    /// <param name="tenantId">第一步所在的租户。</param>
    /// <param name="securityStamp">用户此刻的安全版本；第二步时不一致即作废。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<string> CreateAsync(Guid userId, Guid? tenantId, string securityStamp, CancellationToken cancellationToken)
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var challenge = new TwoFactorChallenge(userId, tenantId, securityStamp, clock.Now + Lifetime, 0);
        await SaveAsync(token, challenge, cancellationToken);
        return token;
    }

    /// <summary>取未过期的挑战；已过期或不存在时返回 null。</summary>
    public async Task<TwoFactorChallenge?> GetAsync(string token, CancellationToken cancellationToken)
    {
        var json = await cache.GetStringAsync(Key(token), cancellationToken);
        var challenge = json is null ? null : JsonSerializer.Deserialize<TwoFactorChallenge>(json);
        return challenge is not null && challenge.ExpiresAt > clock.Now ? challenge : null;
    }

    /// <summary>记一次输错；次数用完时作废挑战并返回 false。</summary>
    public async Task<bool> RecordFailureAsync(string token, TwoFactorChallenge challenge, CancellationToken cancellationToken)
    {
        var attempts = challenge.Attempts + 1;
        if (attempts >= MaxAttempts)
        {
            await RemoveAsync(token, cancellationToken);
            return false;
        }

        await SaveAsync(token, challenge with { Attempts = attempts }, cancellationToken);
        return true;
    }

    public Task RemoveAsync(string token, CancellationToken cancellationToken) =>
        cache.RemoveAsync(Key(token), cancellationToken);

    // 按剩余时长写入：输错重写不延长有效期（此前每次重写都算满，5 次尝试把 5 分钟的挑战拉长到约 25 分钟）。
    // 用相对时长而不是绝对时刻：缓存按自己的系统时钟判断过期，与注入的时钟不必一致
    private async Task SaveAsync(string token, TwoFactorChallenge challenge, CancellationToken cancellationToken)
    {
        var remaining = challenge.ExpiresAt - clock.Now;
        if (remaining <= TimeSpan.Zero)
        {
            await RemoveAsync(token, cancellationToken);
            return;
        }

        await cache.SetStringAsync(
            Key(token),
            JsonSerializer.Serialize(challenge),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = remaining },
            cancellationToken);
    }

    // 键里不放令牌原文：缓存是另一套存储，能读到键名的人不该因此拿到可用的令牌
    private static string Key(string token) =>
        KeyPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

}

/// <summary>一个待完成的第二步。</summary>
/// <param name="UserId">第一步已认出的用户。</param>
/// <param name="TenantId">第一步所在的租户；第二步必须在同一租户下完成。</param>
/// <param name="SecurityStamp">签发时用户的安全版本；第二步时不一致说明凭据已变，挑战作废。</param>
/// <param name="ExpiresAt">到期时刻（UTC），自签发起算，输错不延长。</param>
/// <param name="Attempts">已输错的次数。</param>
internal sealed record TwoFactorChallenge(Guid UserId, Guid? TenantId, string SecurityStamp, DateTime ExpiresAt, int Attempts);
#endif
