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
    internal sealed record Token(string Value, DateTimeOffset? ExpiresAt)
    {
        public DateTimeOffset ObtainedAt { get; init; }
        public DateTimeOffset? RetryAt { get; init; }
    }
    internal static string Key(params string?[] parts) => "leistd:oauth:" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0", parts))));

    internal async ValueTask<Token> GetAsync(string key, TimeSpan buffer,
        Func<CancellationToken, ValueTask<Token>> fetch, CancellationToken cancellationToken, bool renewEarly = false)
    {
        var existing = await ReadAsync(key, cancellationToken);
        if (existing is not null && (!renewEarly || IsFresh(existing))) return existing;

        return await cache.GetOrCreateAsync(key + ":fetch", async ct =>
        {
            // 迟到的 miss 在取得共享工厂后再次读当前状态。
            var current = await ReadAsync(key, ct);
            if (current is not null && (!renewEarly || IsFresh(current))) return current;
            Token token;
            try
            {
                token = (await fetch(ct)) with { ObtainedAt = clock.GetUtcNow() };
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var error = exception as ServiceClientException;
                // 发现配置服务可能包装内层异常，保留已经归一化的来源。
                for (var inner = exception.InnerException; error is null && inner is not null; inner = inner.InnerException)
                    error = inner as ServiceClientException;
                error ??= new ServiceClientException("OpenID Connect token request failed.", exception,
                    exception is OpenIddictExceptions.ProtocolException
                        ? ServiceClientFailureKind.InvalidResponse : ServiceClientFailureClassifier.Classify(exception));
                if (renewEarly && ServiceClientFailureClassifier.IsUnavailable(error))
                {
                    // 401 会删除当前状态；不能用发起回源时捕获的旧令牌复活它。
                    var previous = await ReadAsync(key, ct);
                    if (previous?.ExpiresAt is { } expiry && clock.GetUtcNow() < expiry - buffer)
                    {
                        var retryAt = clock.GetUtcNow().AddSeconds(30);
                        token = previous with { RetryAt = retryAt < expiry - buffer ? retryAt : expiry - buffer };
                        logger.LogWarning("Token endpoint is unavailable; using a token stale for {Staleness}.",
                            clock.GetUtcNow() - (previous.ObtainedAt + (expiry - previous.ObtainedAt) / 2));
                    }
                    else throw error;
                }
                else
                {
                    if (error.FailureKind == ServiceClientFailureKind.RemoteFailure)
                        await RemoveAsync(key, ct);
                    throw error;
                }
            }
            if (token.ExpiresAt is { } expires && expires - clock.GetUtcNow() - buffer is { } ttl && ttl > TimeSpan.Zero)
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

    private bool IsFresh(Token token) => token.RetryAt > clock.GetUtcNow() || token.ExpiresAt is { } expiry &&
        clock.GetUtcNow() < token.ObtainedAt + (expiry - token.ObtainedAt) / 2;

    private ValueTask<Token?> ReadAsync(string key, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync<Token?>(key, static _ => ValueTask.FromResult<Token?>(null), new HybridCacheEntryOptions
        {
            Flags = HybridCacheEntryFlags.DisableDistributedCache | HybridCacheEntryFlags.DisableLocalCacheWrite |
                HybridCacheEntryFlags.DisableUnderlyingData
        }, cancellationToken: cancellationToken);

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
