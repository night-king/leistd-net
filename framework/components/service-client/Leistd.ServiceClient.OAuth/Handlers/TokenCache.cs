using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Leistd.ServiceClient.Exceptions;
using OpenIddict.Abstractions;

namespace Leistd.ServiceClient.OAuth.Handlers;

// 两种认证共用单飞与内存缓存。工厂内按真实到期时间 Set，外层禁止写入，避免固定 TTL 覆盖动态 TTL。
internal sealed class TokenCache(HybridCache cache, ILogger<TokenCache> logger, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    internal sealed record Token(string Value, DateTimeOffset? ExpiresAt);
    internal static string Key(params string?[] parts) => "leistd:oauth:" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0", parts))));

    internal async ValueTask<Token> GetAsync(string key, TimeSpan buffer,
        Func<CancellationToken, ValueTask<Token>> fetch, CancellationToken cancellationToken)
    {
        return await cache.GetOrCreateAsync(key, async ct =>
        {
            // 旧的未命中可能晚于上一轮抓取完成才取得工厂所有权；再次只读检查，避免重复请求。
            var existing = await cache.GetOrCreateAsync<Token?>(key, static _ => ValueTask.FromResult<Token?>(null),
                new HybridCacheEntryOptions
                {
                    Flags = HybridCacheEntryFlags.DisableDistributedCache | HybridCacheEntryFlags.DisableLocalCacheWrite |
                        HybridCacheEntryFlags.DisableUnderlyingData
                }, cancellationToken: ct);
            if (existing is not null) return existing;

            Token token;
            try { token = await fetch(ct); }
            catch (OpenIddictExceptions.ProtocolException exception)
            {
                throw new ServiceClientException($"OpenID Connect token request failed ({exception.Error}).",
                    exception, ServiceClientFailureKind.RemoteFailure);
            }
            // 官方客户端的 expires_in 已取自最终签发主体，无需自行解码 JWT。
            if (token.ExpiresAt is { } expiry && expiry - clock.GetUtcNow() - buffer is { } ttl && ttl > TimeSpan.Zero)
                await cache.SetAsync(key, token, new HybridCacheEntryOptions
                {
                    Flags = HybridCacheEntryFlags.DisableDistributedCache,
                    Expiration = ttl, LocalCacheExpiration = ttl
                }, cancellationToken: ct);
            return token;
        }, new HybridCacheEntryOptions
        {
            Flags = HybridCacheEntryFlags.DisableDistributedCache | HybridCacheEntryFlags.DisableLocalCacheWrite
        }, cancellationToken: cancellationToken);
    }

    internal async ValueTask RemoveAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // 官方 HybridCache 先删除本地条目，再删除后端；删除没有逐条禁用后端的选项。
            await cache.RemoveAsync(key, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            // 本地条目已经失效，后端故障不能覆盖下游响应；并发发生的请求取消仍须传播。
            cancellationToken.ThrowIfCancellationRequested();
            // 只记录异常类型，后端异常消息可能包含缓存键，不传入日志。
            logger.LogWarning("Distributed token cache invalidation failed ({ExceptionType}); the local entry was removed.",
                exception.GetType().Name);
        }
    }
}
