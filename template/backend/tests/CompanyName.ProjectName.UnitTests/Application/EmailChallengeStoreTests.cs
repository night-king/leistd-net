using Leistd.Security.OneTimeCodes.VerificationCodes;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CompanyName.ProjectName.Application.Auth.Dtos;
using CompanyName.ProjectName.Application.Auth.EmailVerification;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Domain.Auth.Errors;
using Leistd.Email.Abstractions;
using Leistd.ExceptionHandling;
using Leistd.Lock.Abstractions;
using Leistd.MultiTenancy.Context;
using Leistd.Timing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CompanyName.ProjectName.UnitTests.Application;

/// <summary>邮箱验证码的发送配额：失败只作废自己的预留，投递成功后不回滚。</summary>
/// <remarks>
/// <para>配额键由所有发往同一邮箱的请求共享。锁租约失效后，旧请求的失败补偿可能晚于新请求的占位，
/// 删共享键就等于删掉别人的配额、绕过限频；因此失败只按本次挑战 Id 写失败标记。</para>
/// <para>缓存是真实 <see cref="MemoryDistributedCache"/>，经 <see cref="MemoryCacheOptions.Clock"/> 接同一假时钟，
/// 配额到期靠推进时钟而不是删键冒充；租约失效与发送阻塞由测试显式编排。</para>
/// </remarks>
public sealed partial class EmailChallengeStoreTests
{
    private static readonly TimeSpan FailSafe = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SendInterval = TimeSpan.FromSeconds(60);

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly LeaseLock _lock = new();
    private readonly GatedCache _cache;
    private readonly ScriptedEmailSender _sender = new();
    private readonly EmailChallengeStore _store;

    public EmailChallengeStoreTests()
    {
        _cache = new GatedCache(new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions { Clock = new CacheClock(_time) })));
        var verificationCode = Options.Create(new VerificationCodeOptions
        {
            Key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray())
        });
        _store = new EmailChallengeStore(
            _cache,
            _lock,
            new HmacVerificationCodeDigest(verificationCode),
            new FixedRegistrationPolicy(),
            _sender,
            NullLogger<EmailChallengeStore>.Instance,
            new UtcClockProvider(_time),
            new CurrentTenant(AsyncLocalCurrentTenantAccessor.Instance),
            verificationCode);
    }

    [Fact]
    public async Task The_send_interval_ends_exactly_at_its_boundary()
    {
        const string email = "boundary@example.test";
        await IssueAsync(email);

        _time.Advance(SendInterval - TimeSpan.FromSeconds(1));
        await AssertTooFrequentAsync(email);

        _time.Advance(TimeSpan.FromSeconds(1));
        await IssueAsync(email);
    }

    [Fact]
    public async Task A_failed_send_voids_its_own_challenge_and_reservation()
    {
        const string email = "failed@example.test";
        _sender.Enqueue(_ => throw new InvalidOperationException("Simulated SMTP failure."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => IssueAsync(email));
        Assert.Null(await _cache.GetAsync(Assert.Single(_cache.ChallengeKeys)));

        var retried = await IssueAsync(email);
        Assert.True(await ValidateAsync(email, retried.ChallengeId, _sender.Calls.Last().Code));
    }

    /// <summary>A 的发送阻塞到配额过期、租约转手；B 占位并发送成功后 A 才失败。</summary>
    [Fact]
    public async Task A_late_failure_cannot_release_the_next_holders_reservation()
    {
        const string email = "late-failure@example.test";
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sender.Enqueue(async _ =>
        {
            aEntered.TrySetResult();
            await releaseA.Task.WaitAsync(FailSafe);
            throw new InvalidOperationException("Simulated SMTP failure.");
        });

        var a = IssueAsync(email);
        await aEntered.Task.WaitAsync(FailSafe);
        _time.Advance(SendInterval);
        _lock.ExpireRateLease();

        var b = await IssueAsync(email);
        releaseA.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => a);

        await AssertTooFrequentAsync(email);
        Assert.True(await ValidateAsync(email, b.ChallengeId, _sender.Calls.Last().Code));
    }

    /// <summary>A 先失败但补偿被延迟；期间配额过期、B 占位，之后 A 的补偿才落地。</summary>
    [Fact]
    public async Task A_delayed_cleanup_cannot_release_the_next_holders_reservation()
    {
        const string email = "delayed-cleanup@example.test";
        _sender.Enqueue(_ =>
        {
            _cache.HoldCleanup();
            throw new InvalidOperationException("Simulated SMTP failure.");
        });

        var a = IssueAsync(email);
        await _cache.CleanupHeld.WaitAsync(FailSafe);
        _time.Advance(SendInterval);
        _lock.ExpireRateLease();

        var b = await IssueAsync(email);
        _cache.ReleaseCleanup();
        await Assert.ThrowsAsync<InvalidOperationException>(() => a);

        await AssertTooFrequentAsync(email);
        Assert.True(await ValidateAsync(email, b.ChallengeId, _sender.Calls.Last().Code));
    }

    [Fact]
    public async Task A_cleanup_failure_does_not_replace_the_send_exception()
    {
        const string email = "cleanup-failure@example.test";
        _cache.FailFailureMarkers = true;
        _sender.Enqueue(_ => throw new InvalidOperationException("Simulated SMTP failure."));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => IssueAsync(email));
        Assert.Equal("Simulated SMTP failure.", exception.Message);
    }

    [Fact]
    public async Task Losing_the_lock_before_sending_skips_the_send()
    {
        const string email = "lost-before-send@example.test";
        _cache.OnChallengeWritten = _lock.ExpireRateLease;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IssueAsync(email));
        Assert.Empty(_sender.Calls);

        _cache.OnChallengeWritten = null;
        await IssueAsync(email);
    }

    [Fact]
    public async Task Losing_the_lock_during_the_send_neither_aborts_nor_rolls_it_back()
    {
        const string email = "lost-during-send@example.test";
        var tokenCanceled = true;
        _sender.Enqueue(token =>
        {
            _lock.ExpireRateLease();
            tokenCanceled = token.IsCancellationRequested;
            return Task.CompletedTask;
        });

        var issued = await IssueAsync(email);

        Assert.False(tokenCanceled);
        await AssertTooFrequentAsync(email);
        Assert.True(await ValidateAsync(email, issued.ChallengeId, _sender.Calls.Single().Code));
    }

    [Fact]
    public async Task Canceling_the_request_during_the_send_aborts_it_and_voids_the_reservation()
    {
        const string email = "canceled-during-send@example.test";
        using var request = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sender.Enqueue(async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).WaitAsync(FailSafe);
        });

        var issuing = IssueAsync(email, request.Token);
        await entered.Task.WaitAsync(FailSafe);
        await request.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => issuing);
        Assert.Null(await _cache.GetAsync(Assert.Single(_cache.ChallengeKeys)));
        await IssueAsync(email);
    }

    [Fact]
    public async Task A_request_canceled_after_a_successful_send_keeps_the_challenge_and_quota()
    {
        const string email = "canceled-after-send@example.test";
        using var request = new CancellationTokenSource();
        _sender.Enqueue(_ =>
        {
            request.Cancel();
            return Task.CompletedTask;
        });

        var issued = await IssueAsync(email, request.Token);

        await AssertTooFrequentAsync(email);
        Assert.True(await ValidateAsync(email, issued.ChallengeId, _sender.Calls.Single().Code));
    }

    private Task<EmailVerificationChallengeOutputDto> IssueAsync(string email, CancellationToken cancellationToken = default)
        => _store.IssueRegistrationAsync(email, FixedRegistrationPolicy.Policy, cancellationToken);

    private Task<bool> ValidateAsync(string email, Guid challengeId, string code)
        => _store.ValidateRegistrationAsync(email, new EmailVerificationInputDto { ChallengeId = challengeId, Code = code });

    private async Task AssertTooFrequentAsync(string email)
    {
        var exception = await Assert.ThrowsAsync<BusinessException>(() => IssueAsync(email));
        Assert.Equal(AuthErrorCodes.EmailCodeSendTooFrequent, exception.Code);
    }

    private sealed class CacheClock(TimeProvider time) : ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }

    /// <summary>按键互斥的锁；测试可让发送配额锁的当前租约失效：持有者收到 LockLost，锁随即可被他人获取。</summary>
    private sealed class LeaseLock : IDistributedLock
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Lease> _holders = new(StringComparer.Ordinal);

        public async Task<ILockHandle> LockAsync(string key, CancellationToken cancellationToken = default)
        {
            var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            var lease = new Lease(gate);
            _holders[key] = lease;
            return lease;
        }

        public Task<ILockHandle?> TryLockAsync(string key, TimeSpan timeout, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void ExpireRateLease()
        {
            var key = Assert.Single(_holders.Keys, k => k.Contains(":lock:rate:", StringComparison.Ordinal));
            Assert.True(_holders.TryRemove(key, out var lease));
            lease.Expire();
        }

        private sealed class Lease(SemaphoreSlim gate) : ILockHandle
        {
            private readonly CancellationTokenSource _lost = new();
            private int _released;

            public CancellationToken LockLost => _lost.Token;

            public void Expire()
            {
                _lost.Cancel();
                Release();
            }

            public ValueTask DisposeAsync()
            {
                Release();
                return ValueTask.CompletedTask;
            }

            private void Release()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    gate.Release();
                }
            }
        }
    }

    /// <summary>真实缓存外加编排点：挂住失败补偿、让失败标记写入失败、在挑战写入后回调。</summary>
    private sealed class GatedCache(IDistributedCache inner) : IDistributedCache
    {
        private TaskCompletionSource? _cleanupGate;
        private readonly TaskCompletionSource _cleanupHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _challengeKeys = new();

        public Task CleanupHeld => _cleanupHeld.Task;

        public bool FailFailureMarkers { get; set; }

        public Action? OnChallengeWritten { get; set; }

        /// <summary>写入过的挑战键，按写入顺序。</summary>
        public IReadOnlyList<string> ChallengeKeys => [.. _challengeKeys];

        /// <summary>此后的删除与失败标记写入停住，直到 <see cref="ReleaseCleanup"/>；新请求的占位不受影响。</summary>
        public void HoldCleanup() => _cleanupGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseCleanup() => _cleanupGate?.TrySetResult();

        public byte[]? Get(string key) => inner.Get(key);

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => inner.GetAsync(key, token);

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => inner.Set(key, value, options);

        public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            if (IsFailureMarker(key))
            {
                if (FailFailureMarkers)
                {
                    throw new InvalidOperationException("Simulated cache failure.");
                }

                await WaitForCleanupAsync();
            }

            await inner.SetAsync(key, value, options, token);
            if (key.Contains(":challenge:", StringComparison.Ordinal))
            {
                _challengeKeys.Enqueue(key);
                OnChallengeWritten?.Invoke();
            }
        }

        public void Refresh(string key) => inner.Refresh(key);

        public Task RefreshAsync(string key, CancellationToken token = default) => inner.RefreshAsync(key, token);

        public void Remove(string key) => inner.Remove(key);

        public async Task RemoveAsync(string key, CancellationToken token = default)
        {
            await WaitForCleanupAsync();
            await inner.RemoveAsync(key, token);
        }

        private async Task WaitForCleanupAsync()
        {
            if (_cleanupGate is { } gate)
            {
                _cleanupHeld.TrySetResult();
                await gate.Task.WaitAsync(FailSafe);
            }
        }

        private static bool IsFailureMarker(string key) => key.Contains(":rate-failed:", StringComparison.Ordinal);
    }

    /// <summary>按入队顺序执行每次发送的行为，队列空时直接成功；记录每封信里的挑战码。</summary>
    private sealed partial class ScriptedEmailSender : IEmailSender
    {
        private readonly ConcurrentQueue<Func<CancellationToken, Task>> _script = new();
        private readonly ConcurrentQueue<SentCode> _calls = new();

        public IReadOnlyList<SentCode> Calls => [.. _calls];

        public void Enqueue(Func<CancellationToken, Task> behavior) => _script.Enqueue(behavior);

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            _calls.Enqueue(new SentCode(CodeRegex().Match(message.Body).Groups[1].Value));
            return _script.TryDequeue(out var behavior) ? behavior(cancellationToken) : Task.CompletedTask;
        }

        [GeneratedRegex(@">(\d{6})<", RegexOptions.CultureInvariant)]
        private static partial Regex CodeRegex();
    }

    private sealed record SentCode(string Code);

    private sealed class FixedRegistrationPolicy : IUserRegistrationPolicyProvider
    {
        public static readonly UserRegistrationPolicy Policy = new()
        {
            Enabled = true,
            CaptchaExpiryMinutes = 5,
            EnableEmailVerification = true,
            EmailCodeExpiryMinutes = 5,
            EmailCodeSendIntervalSeconds = (int)SendInterval.TotalSeconds,
            EmailCodeMaxAttempts = 5,
        };

        public Task<UserRegistrationPolicy> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Policy);
    }
}
