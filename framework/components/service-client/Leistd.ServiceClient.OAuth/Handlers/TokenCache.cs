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
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private static readonly HybridCacheEntryOptions InFlightOptions = new()
    {
        Flags = HybridCacheEntryFlags.DisableDistributedCache | HybridCacheEntryFlags.DisableLocalCacheWrite
    };
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

        var pending = cache.GetOrCreateAsync(key + ":fetch", async ct =>
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
                    var previous = await UseStaleAsync(key, buffer, false, ct);
                    if (previous is null) throw error;
                    return previous;
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
        }, InFlightOptions, cancellationToken: cancellationToken).AsTask();
        if (!renewEarly || existing is null) return await pending;
        try
        {
            return await pending.WaitAsync(WaitBudget, clock, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            if (pending.IsCompleted) return await pending;
            _ = pending.ContinueWith(static completed => { _ = completed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();
            var previous = await UseStaleAsync(key, buffer, true, cancellationToken);
            return previous ?? await pending;
        }
    }

    // 401 删除之后，超时和后台失败都必须重读当前令牌；单飞只协调节流转换，不保留协调条目。
    private ValueTask<Token?> UseStaleAsync(string key, TimeSpan buffer, bool timedOut, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync<Token?>(key + ":stale", async ct =>
        {
            var previous = await ReadAsync(key, ct);
            var now = clock.GetUtcNow();
            if (previous?.ExpiresAt is not { } expiry || now >= expiry - buffer) return null;
            if (timedOut && IsFresh(previous)) return previous;
            var retry = now + RetryDelay;
            var result = previous with { RetryAt = retry < expiry - buffer ? retry : expiry - buffer };
            await cache.SetAsync(key, result, new HybridCacheEntryOptions
            {
                Flags = HybridCacheEntryFlags.DisableDistributedCache,
                Expiration = expiry - buffer - now, LocalCacheExpiration = expiry - buffer - now
            }, cancellationToken: ct);
            if (previous.RetryAt is null || now >= previous.RetryAt)
                logger.LogWarning("Token endpoint exceeded its wait budget or is unavailable; using a token stale for {Staleness} while validation may continue.",
                    now - (previous.ObtainedAt + (expiry - previous.ObtainedAt) / 2));
            return result;
        }, InFlightOptions, cancellationToken: cancellationToken);

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
