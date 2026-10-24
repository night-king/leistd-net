#if (LocalIdentity)
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Leistd.Timing;
using Microsoft.Extensions.Caching.Distributed;

namespace CompanyName.ProjectName.Application.Auth.Passkeys;

/// <summary>进行中的通行密钥仪式：生成选项之后、浏览器交回响应之前，服务端保存的那一份状态。</summary>
/// <remarks>
/// <para>仪式状态里有挑战：只能放在服务端，交给浏览器的只有选项。完成时取出并删除，同一份状态只认一次。</para>
/// <para>登记与确认身份按"本人 + 会话"一个槽位：重新开始即覆盖旧的，别的会话拿不到。
/// 免口令登录还没有用户，按服务端签发的随机凭据保存，绑定发起时的租户。登录第二步的状态记在第二步挑战上，不在这里。</para>
/// <para>有效期从开始那一刻算起，按注入的时钟判定；缓存条目的过期只负责回收。</para>
/// </remarks>
internal sealed class PasskeyCeremonyStore(IDistributedCache cache, IClock clock)
{
    /// <summary>仪式的有效期。</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private const string KeyPrefix = "auth:passkey:";

    /// <summary>保存本人在这个会话里的一次登记或确认身份，返回时覆盖之前未完成的同类仪式。</summary>
    public Task SaveForSessionAsync(
        PasskeyCeremonyPurpose purpose,
        Guid userId,
        Guid? sessionId,
        string state,
        string securityStamp,
        CancellationToken cancellationToken) =>
        SaveAsync(SessionKey(purpose, userId, sessionId), new StoredCeremony(state, securityStamp, null, clock.Now + Lifetime), cancellationToken);

    /// <summary>取出并删除本人在这个会话里的仪式；没有、已过期或安全版本已变时返回 null。</summary>
    public async Task<string?> TakeForSessionAsync(
        PasskeyCeremonyPurpose purpose,
        Guid userId,
        Guid? sessionId,
        string securityStamp,
        CancellationToken cancellationToken)
    {
        var stored = await TakeAsync(SessionKey(purpose, userId, sessionId), cancellationToken);
        return stored is not null && string.Equals(stored.SecurityStamp, securityStamp, StringComparison.Ordinal) ? stored.State : null;
    }

    /// <summary>保存一次免口令登录，返回交给浏览器的凭据。</summary>
    public async Task<string> SaveSignInAsync(string state, Guid? tenantId, CancellationToken cancellationToken)
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        await SaveAsync(SignInKey(token), new StoredCeremony(state, null, tenantId, clock.Now + Lifetime), cancellationToken);
        return token;
    }

    /// <summary>取出并删除一次免口令登录；没有、已过期或不是这个租户发起的返回 null。</summary>
    public async Task<string?> TakeSignInAsync(string token, Guid? tenantId, CancellationToken cancellationToken)
    {
        var stored = await TakeAsync(SignInKey(token), cancellationToken);
        return stored is not null && stored.TenantId == tenantId ? stored.State : null;
    }

    private async Task SaveAsync(string key, StoredCeremony ceremony, CancellationToken cancellationToken) =>
        await cache.SetStringAsync(
            key,
            JsonSerializer.Serialize(ceremony),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime },
            cancellationToken);

    private async Task<StoredCeremony?> TakeAsync(string key, CancellationToken cancellationToken)
    {
        var json = await cache.GetStringAsync(key, cancellationToken);
        if (json is null)
            return null;

        await cache.RemoveAsync(key, cancellationToken);
        var stored = JsonSerializer.Deserialize<StoredCeremony>(json);
        return stored is not null && stored.ExpiresAt > clock.Now ? stored : null;
    }

    private static string SessionKey(PasskeyCeremonyPurpose purpose, Guid userId, Guid? sessionId) =>
        $"{KeyPrefix}{purpose.ToString().ToLowerInvariant()}:{userId:N}:{sessionId?.ToString("N") ?? "-"}";

    // 键里不放凭据原文：缓存是另一套存储，能读到键名的人不该因此拿到可用的凭据
    private static string SignInKey(string token) =>
        KeyPrefix + "sign-in:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed record StoredCeremony(string State, string? SecurityStamp, Guid? TenantId, DateTime ExpiresAt);
}

/// <summary>按会话保存的仪式用途。</summary>
internal enum PasskeyCeremonyPurpose
{
    /// <summary>登记一把新的通行密钥。</summary>
    Registration,

    /// <summary>用通行密钥确认身份。</summary>
    Reauthentication
}
#endif
