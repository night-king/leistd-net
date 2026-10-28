#if (RemoteTokenAuth)
using System.Collections.Concurrent;
using System.Net;
using CompanyName.ProjectName.Domain.Users.Abstractions;
using Leistd.ServiceClient.Exceptions;
using Leistd.ServiceClient.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CompanyName.ProjectName.Infrastructure.AccountPreferences;

/// <summary>向 Identity 读取账户偏好的进程内缓存：按（租户，用户）保存最近一次成功取得的完整表示与 ETag。</summary>
/// <remarks>
/// <para>两类读取：</para>
/// <list type="bullet">
/// <item>前台（当前用户的 <c>/me</c>）：每次自己发一次条件请求，不受新鲜期限制，也不加入在途回源——
/// 本进程看不到其他实例或 Identity 上的写入，加入别人早先发出的回源可能拿到写入之前的值。</item>
/// <item>服务端（收件人文本等）：新鲜期内直接用缓存；过期后同 key、同代次的并发读取合并为一次回源。</item>
/// </list>
/// <para>每个 key 有代次：本人经本实例写入成功（换成 Identity 返回的新表示），或机器身份被拒（清除旧表示），代次加一。
/// 发起时代次已变的读取返回后，结果既不进缓存、也不交给调用方，而是按当前状态重新读取。
/// 代次不变时按回源发出的先后：先发出、后返回的读取不覆盖后发出的读取已发布的值。</para>
/// <para>上游连接失败、超时或 5xx 时用旧表示并记 Warning；没有旧表示时返回 <see langword="null"/>（暂时读不到），
/// 不拿本地默认值冒充。机器身份被拒（取令牌被拒、401、403）清除旧表示并抛出，按依赖故障处理，之后再遇 5xx 也不恢复旧值。</para>
/// <para>只放进程内：值含个人偏好，失效又依赖本进程的写入代次。旧表示保留 <see cref="AccountPreferencesClientOptions.RetainFor"/>，
/// 过期的表示在访问时丢弃；闲置超过同一时长、且没有在途读取的条目（连同代次与序号）由读写入口顺带清扫，
/// 每个 <see cref="AccountPreferencesClientOptions.FreshFor"/> 至多一次。清扫先在条目锁内标记退役再移除，
/// 拿到退役条目的调用改取新条目，同一 key 不会同时有两个协调对象。</para>
/// </remarks>
internal sealed class IdentityAccountPreferenceCache(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<AccountPreferencesClientOptions> options,
    TimeProvider timeProvider,
    ILogger<IdentityAccountPreferenceCache> logger)
{
    // 代次变化后重读的上限：每次重读都在一次写入或认证失效之后，正常不会连续发生
    private const int MaxAttempts = 3;

    private readonly ConcurrentDictionary<AccountPreferenceCacheKey, Entry> _entries = new();

    // 下一次清扫的时刻（UTC ticks）
    private long _nextSweepTicks;

    /// <summary>当前保留的条目数（测试用）。</summary>
    internal int Count => _entries.Count;

    /// <summary>当前用户的读取：自己发条件请求，不加入在途回源。</summary>
    public async Task<AccountPreferenceSnapshot?> ReadForegroundAsync(AccountPreferenceCacheKey key, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var (entry, generation, sequence, previous) = Snapshot(key);
            var outcome = await FetchAsync(key, entry, generation, sequence, previous, cancellationToken);
            if (outcome.Current)
            {
                return outcome.Value;
            }
        }

        return Fallback(key);
    }

    /// <summary>服务端读取：新鲜期内用缓存；过期后同代次的并发读取合并为一次回源。</summary>
    public async Task<AccountPreferenceSnapshot?> ReadServerAsync(AccountPreferenceCacheKey key, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var entry = Acquire(key);
            Task<Outcome> shared;
            lock (entry)
            {
                if (entry.Retired)
                {
                    attempt--;
                    continue;
                }

                var current = Live(entry);
                if (current is not null && timeProvider.GetUtcNow() - current.FetchedAt < options.CurrentValue.FreshFor)
                {
                    return current.Value;
                }

                if (entry.ServerRead is { } running && entry.ServerReadGeneration == entry.Generation)
                {
                    shared = running;
                }
                else
                {
                    // 共享回源不绑任何一个调用方的取消：一个等待者取消不能牵连其他等待者；时限由客户端的弹性管道给出
                    var generation = entry.Generation;
                    entry.InFlight++;
                    shared = FetchAsync(key, entry, generation, ++entry.Started, current, CancellationToken.None);
                    entry.ServerRead = shared;
                    entry.ServerReadGeneration = generation;
                    // 同步完成时回调就地执行（锁可重入），所以上面先用局部变量拿住任务
                    _ = shared.ContinueWith(
                        completed => Release(entry, completed),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }

            var outcome = await shared.WaitAsync(cancellationToken);
            if (outcome.Current)
            {
                return outcome.Value;
            }
        }

        return Fallback(key);
    }

    /// <summary>本人经本实例写入成功：代次加一，换成 Identity 返回的新表示。在途的旧读取返回后不再发布。</summary>
    public void StoreAfterWrite(AccountPreferenceCacheKey key, AccountPreferenceSnapshot value, string etag)
    {
        while (true)
        {
            var entry = Acquire(key);
            lock (entry)
            {
                if (entry.Retired)
                {
                    continue;
                }

                entry.Generation++;
                entry.Published = ++entry.Started;
                entry.Current = new Representation(value, etag, timeProvider.GetUtcNow());
                return;
            }
        }
    }

    // 读一次 Identity；Current 为 false 表示发起后代次已变，结果作废，由调用方按新状态重读
    private async Task<Outcome> FetchAsync(
        AccountPreferenceCacheKey key,
        Entry entry,
        long generation,
        long sequence,
        Representation? previous,
        CancellationToken cancellationToken)
    {
        try
        {
            return await FetchCoreAsync(key, entry, generation, sequence, previous, cancellationToken);
        }
        finally
        {
            lock (entry)
            {
                entry.InFlight--;
            }
        }
    }

    private async Task<Outcome> FetchCoreAsync(
        AccountPreferenceCacheKey key,
        Entry entry,
        long generation,
        long sequence,
        Representation? previous,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var api = scope.ServiceProvider.GetRequiredService<IIdentityAccountPreferencesReadApi>();
            response = await api.GetAsync(key.TenantSegment, key.UserId, previous?.ETag, cancellationToken);
        }
        catch (ServiceClientException ex) when (ServiceClientFailureClassifier.IsUnavailable(ex))
        {
            return UseStale(entry, generation, previous, ex);
        }
        catch (ServiceClientException ex) when (ex.FailureKind == ServiceClientFailureKind.RemoteFailure)
        {
            // 取令牌被签发方拒绝：机器身份或授予配置有误，旧值不能再用
            Invalidate(entry, generation, ex);
            throw;
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                {
                    var value = (await response.ReadContentAsync<IdentityAccountPreferencesResponse>(cancellationToken: cancellationToken))?.ToSnapshot()
                                ?? throw new ServiceClientException("Identity returned an empty account preference representation.", failureKind: ServiceClientFailureKind.InvalidResponse);
                    var etag = response.Headers.ETag?.ToString()
                               ?? throw new ServiceClientException("Identity returned account preferences without an ETag.", failureKind: ServiceClientFailureKind.InvalidResponse);
                    return Publish(entry, generation, sequence, new Representation(value, etag, timeProvider.GetUtcNow()));
                }
                case HttpStatusCode.NotModified when previous is not null:
                    return Renew(entry, generation, sequence, previous);
                case HttpStatusCode.NotFound:
                    // 用户或租户在 Identity 不存在：没有可用的偏好，按暂时读不到处理
                    logger.LogWarning("Identity has no account preferences for user {UserId} in {Tenant}", key.UserId, key.TenantSegment);
                    return Discard(entry, generation);
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                {
                    var rejection = await response.CreateRemoteErrorAsync(cancellationToken);
                    Invalidate(entry, generation, rejection);
                    throw rejection;
                }
                // 与 5xx 同属暂时性上游故障：网关超时、限流（读取管道已重试过）
                case HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError:
                    return UseStale(entry, generation, previous, await response.CreateRemoteErrorAsync(cancellationToken));
                default:
                    throw await response.CreateRemoteErrorAsync(cancellationToken);
            }
        }
    }

    private Outcome Publish(Entry entry, long generation, long sequence, Representation representation)
    {
        lock (entry)
        {
            if (entry.Generation != generation)
            {
                return Outcome.Stale;
            }

            // 比已发布的那次更早发出的读取：Identity 当时的值可能已被后发出的读取取代，不覆盖缓存，交出当前的
            if (sequence < entry.Published)
            {
                return new Outcome(true, Live(entry)?.Value ?? representation.Value);
            }

            entry.Published = sequence;
            entry.Current = representation;
            return new Outcome(true, representation.Value);
        }
    }

    // 304：只在发起时的那份旧表示仍在时续期
    private Outcome Renew(Entry entry, long generation, long sequence, Representation previous)
    {
        lock (entry)
        {
            if (entry.Generation != generation || !ReferenceEquals(entry.Current, previous))
            {
                return Outcome.Stale;
            }

            entry.Published = Math.Max(entry.Published, sequence);
            entry.Current = previous with { FetchedAt = timeProvider.GetUtcNow() };
            return new Outcome(true, previous.Value);
        }
    }

    private Outcome UseStale(Entry entry, long generation, Representation? previous, Exception failure)
    {
        lock (entry)
        {
            if (entry.Generation != generation)
            {
                return Outcome.Stale;
            }

            // 发起时捕获的旧表示须仍是当前的那份：期间被认证失效清除或被写入替换，就不能拿它回落
            var usable = previous is not null && ReferenceEquals(entry.Current, previous) ? Live(entry) : null;
            if (usable is null)
            {
                logger.LogWarning(failure, "Identity is unavailable and no earlier account preferences are cached; preferences are unknown for now");
            }
            else
            {
                logger.LogWarning(failure, "Identity is unavailable; using the account preferences fetched earlier");
            }

            return new Outcome(true, usable?.Value);
        }
    }

    private Outcome Discard(Entry entry, long generation)
    {
        lock (entry)
        {
            if (entry.Generation != generation)
            {
                return Outcome.Stale;
            }

            entry.Current = null;
            return new Outcome(true, null);
        }
    }

    // 机器身份被拒：代次加一并清除旧表示，此前发起的读取一律作废，之后的 5xx 也拿不到旧值
    private void Invalidate(Entry entry, long generation, Exception failure)
    {
        lock (entry)
        {
            entry.Generation++;
            entry.Current = null;
        }

        logger.LogError(failure, "Identity rejected this service's identity while reading account preferences (generation {Generation})", generation);
    }

    // 连续因代次变化重读仍未成功：交出当前已有的新鲜表示（来自写入或另一次成功读取），没有就算暂时读不到
    private AccountPreferenceSnapshot? Fallback(AccountPreferenceCacheKey key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        lock (entry)
        {
            return Live(entry)?.Value;
        }
    }

    // 调用方随后必须调用 FetchAsync：在途计数在这里加一，由它减回
    private (Entry Entry, long Generation, long Sequence, Representation? Previous) Snapshot(AccountPreferenceCacheKey key)
    {
        while (true)
        {
            var entry = Acquire(key);
            lock (entry)
            {
                if (entry.Retired)
                {
                    continue;
                }

                entry.InFlight++;
                return (entry, entry.Generation, ++entry.Started, Live(entry));
            }
        }
    }

    // 取 key 的条目并记下访问时刻，顺带按节流清扫闲置条目；调用方在锁内检查 Retired，退役了就重取
    private Entry Acquire(AccountPreferenceCacheKey key)
    {
        var now = timeProvider.GetUtcNow();
        SweepIfDue(now);
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        lock (entry)
        {
            entry.LastAccess = now;
        }

        return entry;
    }

    private void SweepIfDue(DateTimeOffset now)
    {
        var due = Interlocked.Read(ref _nextSweepTicks);
        if (now.UtcTicks < due
            || Interlocked.CompareExchange(ref _nextSweepTicks, (now + options.CurrentValue.FreshFor).UtcTicks, due) != due)
        {
            return;
        }

        var retainFor = options.CurrentValue.RetainFor;
        foreach (var (key, entry) in _entries)
        {
            lock (entry)
            {
                // 有在途读取的条目还被持有：代次与序号要留给它们比较，不能换成新条目
                if (entry.InFlight > 0 || entry.ServerRead is not null || now - entry.LastAccess < retainFor)
                {
                    continue;
                }

                entry.Retired = true;
                _entries.TryRemove(new KeyValuePair<AccountPreferenceCacheKey, Entry>(key, entry));
            }
        }
    }

    // 调用方持有 entry 的锁
    private Representation? Live(Entry entry)
    {
        if (entry.Current is { } current && timeProvider.GetUtcNow() - current.FetchedAt >= options.CurrentValue.RetainFor)
        {
            entry.Current = null;
        }

        return entry.Current;
    }

    private static void Release(Entry entry, Task<Outcome> completed)
    {
        lock (entry)
        {
            if (ReferenceEquals(entry.ServerRead, completed))
            {
                entry.ServerRead = null;
            }
        }
    }

    private sealed class Entry
    {
        public long Generation;
        // 回源发出的序号与已发布结果的序号：先发出、后返回的读取不覆盖后发出的读取已发布的值
        public long Started;
        public long Published;
        public Representation? Current;
        public Task<Outcome>? ServerRead;
        public long ServerReadGeneration;
        // 进行中的回源数、最近一次访问的时刻、是否已被清扫移出字典
        public int InFlight;
        public DateTimeOffset LastAccess;
        public bool Retired;
    }

    private sealed record Representation(AccountPreferenceSnapshot Value, string ETag, DateTimeOffset FetchedAt);

    private readonly record struct Outcome(bool Current, AccountPreferenceSnapshot? Value)
    {
        public static Outcome Stale => new(false, null);
    }
}
#endif
