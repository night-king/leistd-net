using Leistd.Security.OneTimeCodes.VerificationCodes;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CompanyName.ProjectName.Application.Auth.Dtos;
using CompanyName.ProjectName.Application.Auth.EmailVerification;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Domain.Auth.Errors;
using Leistd.BackgroundJobs.Queues;
using Leistd.Email.Abstractions;
using Leistd.ExceptionHandling;
using Leistd.Lock.Abstractions;
using Leistd.MultiTenancy.Context;
#if (IncludeLocalization)
using CompanyName.ProjectName.Application.Settings.Dtos;
using CompanyName.ProjectName.Application.Settings.Preferences;
#endif
using Leistd.Timing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Internal;
#if (IncludeLocalization)
using Microsoft.Extensions.Localization;
#endif
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
    private readonly CapturingQueue _queue = new();
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
            _queue,
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

    /// <summary>找回口令：不论邮箱是否对应账号都签发挑战、占用配额；只有可投递的账号才在后台收到码。</summary>
    [Fact]
    public async Task A_password_reset_without_a_target_sends_nothing_and_never_validates()
    {
        const string email = "nobody@example.test";
        var issued = await _store.IssuePasswordResetAsync(email, target: null, FixedRegistrationPolicy.Policy);

        Assert.Empty(_queue.Items);
        await AssertPasswordResetTooFrequentAsync(email, target: null);
        foreach (var code in new[] { "000000", "123456", "999999" })
        {
            Assert.Null(await ValidatePasswordResetAsync(email, issued.ChallengeId, code));
        }
    }

    [Fact]
    public async Task A_password_reset_code_is_sent_in_the_background_and_proves_the_user_and_stamp()
    {
        const string email = "known@example.test";
        var target = new EmailCodeRecipient(Guid.NewGuid(), email, "stamp-1");
        var issued = await _store.IssuePasswordResetAsync(email, target, FixedRegistrationPolicy.Policy);
        Assert.Empty(_sender.Calls);

        await _queue.RunAllAsync(_sender, _time);
        var code = Assert.Single(_sender.Calls).Code;

        // 用途隔离：找回码不能拿去注册
        Assert.False(await ValidateAsync(email, issued.ChallengeId, code));
        Assert.Equal(
            new EmailCodeProof(target.UserId, target.SecurityStamp),
            await ValidatePasswordResetAsync(email.ToUpperInvariant(), issued.ChallengeId, code));
        Assert.Null(await ValidatePasswordResetAsync(email, issued.ChallengeId, code));
    }

    /// <summary>排队跨过挑战到期就不再发：发出去的码也用不了。</summary>
    [Fact]
    public async Task A_password_reset_code_that_expired_in_the_queue_is_not_sent()
    {
        const string email = "late@example.test";
        await _store.IssuePasswordResetAsync(
            email, new EmailCodeRecipient(Guid.NewGuid(), email, "stamp"), FixedRegistrationPolicy.Policy);

        _time.Advance(TimeSpan.FromMinutes(FixedRegistrationPolicy.Policy.EmailCodeExpiryMinutes));
        await _queue.RunAllAsync(_sender, _time);

        Assert.Empty(_sender.Calls);
    }

    /// <summary>发送失败只记日志：配额不退，响应早已返回，失败与否不能让人看出邮箱对应着账号。</summary>
    [Fact]
    public async Task A_failed_password_reset_delivery_keeps_the_quota()
    {
        const string email = "smtp-down@example.test";
        _sender.Enqueue(_ => throw new InvalidOperationException("Simulated SMTP failure."));
        await _store.IssuePasswordResetAsync(
            email, new EmailCodeRecipient(Guid.NewGuid(), email, "stamp"), FixedRegistrationPolicy.Policy);

        await _queue.RunAllAsync(_sender, _time);

        await AssertPasswordResetTooFrequentAsync(email, target: null);
    }

    /// <summary>
    /// 下游自己的超时或取消（消费者并没停机）也只是投递失败：记安全日志后吞掉，不让原始异常落到队列的通用失败日志里；
    /// 消费者停机时的取消照常传出去。
    /// </summary>
    [Fact]
    public async Task Only_a_stopping_consumer_lets_a_canceled_delivery_escape()
    {
        const string email = "canceled@example.test";
        var target = new EmailCodeRecipient(Guid.NewGuid(), email, "stamp");
        _sender.Enqueue(_ => throw new TaskCanceledException("SMTP timed out."));
        await _store.IssuePasswordResetAsync(email, target, FixedRegistrationPolicy.Policy);

        await _queue.RunAllAsync(_sender, _time);

        _time.Advance(SendInterval);
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        _sender.Enqueue(token => Task.FromCanceled(token));
        await _store.IssuePasswordResetAsync(email, target, FixedRegistrationPolicy.Policy);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _queue.RunAllAsync(_sender, _time, stopping.Token));
    }

    /// <summary>窗口从本窗口第一次发码起算 24 小时，期间至多发 10 次；到期后重新计数。</summary>
    [Fact]
    public async Task Password_reset_codes_are_capped_per_fixed_window()
    {
        const string email = "capped@example.test";
        for (var i = 0; i < EmailChallengeStore.PasswordResetLimitPerWindow; i++)
        {
            await _store.IssuePasswordResetAsync(email, target: null, FixedRegistrationPolicy.Policy);
            _time.Advance(SendInterval);
        }

        await AssertPasswordResetTooFrequentAsync(email, target: null);

        // 窗口自第一次发码起算：到期前一秒仍被拒，到期即放行
        _time.Advance(EmailChallengeStore.PasswordResetWindow
            - SendInterval * EmailChallengeStore.PasswordResetLimitPerWindow - TimeSpan.FromSeconds(1));
        await AssertPasswordResetTooFrequentAsync(email, target: null);
        _time.Advance(TimeSpan.FromSeconds(1));
        await _store.IssuePasswordResetAsync(email, target: null, FixedRegistrationPolicy.Policy);
    }

    /// <summary>上限只管找回：注册发码不计入，也不受它限制（发送间隔仍共用）。</summary>
    [Fact]
    public async Task The_password_reset_cap_does_not_count_registration_codes()
    {
        const string email = "shared@example.test";
        for (var i = 0; i < EmailChallengeStore.PasswordResetLimitPerWindow; i++)
        {
            await IssueAsync(email);
            _time.Advance(SendInterval);
        }

        await _store.IssuePasswordResetAsync(email, target: null, FixedRegistrationPolicy.Policy);
    }

    /// <summary>登录第二步的码在后台投递：签发时不发信，队列跑完才到；挑战绑定账号与安全版本。</summary>
    [Fact]
    public async Task A_sign_in_code_is_sent_in_the_background_and_proves_the_user_and_stamp()
    {
        var recipient = new EmailCodeRecipient(Guid.NewGuid(), "signin@example.test", "stamp-1");
        var issued = await _store.IssueTwoFactorSignInAsync(recipient);
        Assert.Empty(_sender.Calls);

        await _queue.RunAllAsync(_sender, _time);
        var code = Assert.Single(_sender.Calls).Code;

        var check = await ValidateSignInAsync(recipient.Email, issued.ChallengeId, code);
        Assert.Equal(EmailCodeOutcome.Valid, check.Outcome);
        Assert.Equal(new EmailCodeProof(recipient.UserId, recipient.SecurityStamp), check.Proof);
    }

    /// <summary>发送间隔内再次发码沿用上一封：同一个挑战、回报剩余间隔、不另发信；别的账号或凭据变了之后不沿用。</summary>
    [Fact]
    public async Task A_sign_in_send_within_the_interval_reuses_the_previous_code()
    {
        var recipient = new EmailCodeRecipient(Guid.NewGuid(), "reuse@example.test", "stamp-1");
        var first = await _store.IssueTwoFactorSignInAsync(recipient);
        _time.Advance(TimeSpan.FromSeconds(20));

        var again = await _store.IssueTwoFactorSignInAsync(recipient);

        Assert.Equal(first.ChallengeId, again.ChallengeId);
        Assert.Equal((int)(SendInterval - TimeSpan.FromSeconds(20)).TotalSeconds, again.RetryAfterSeconds);
        Assert.Single(_queue.Items);

        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => _store.IssueTwoFactorSignInAsync(recipient with { SecurityStamp = "stamp-2" }));
        Assert.Equal(AuthErrorCodes.EmailCodeSendTooFrequent, exception.Code);
    }

    /// <summary>登录第二步的发送间隔单独一把键：找回口令往同一地址发码不挡它，它也不挡找回口令。</summary>
    [Fact]
    public async Task Sign_in_codes_have_their_own_send_interval()
    {
        var recipient = new EmailCodeRecipient(Guid.NewGuid(), "separate@example.test", "stamp-1");
        await _store.IssuePasswordResetAsync(recipient.Email, target: null, FixedRegistrationPolicy.Policy);
        await _store.IssueTwoFactorSignInAsync(recipient);

        var other = recipient with { Email = "separate-2@example.test" };
        await _store.IssueTwoFactorSignInAsync(other);
        await _store.IssuePasswordResetAsync(other.Email, target: null, FixedRegistrationPolicy.Policy);
    }

    /// <summary>码失效与码不对分开报：挑战不存在或次数已用完是失效，这一次输错是码不对。</summary>
    [Fact]
    public async Task Validation_tells_an_expired_challenge_from_a_wrong_code()
    {
        var recipient = new EmailCodeRecipient(Guid.NewGuid(), "outcome@example.test", "stamp-1");
        Assert.Equal(EmailCodeOutcome.Expired, (await ValidateSignInAsync(recipient.Email, Guid.NewGuid(), "000000")).Outcome);

        var issued = await _store.IssueTwoFactorSignInAsync(recipient);
        for (var attempt = 0; attempt < FixedRegistrationPolicy.Policy.EmailCodeMaxAttempts; attempt++)
        {
            Assert.Equal(EmailCodeOutcome.Wrong, (await ValidateSignInAsync(recipient.Email, issued.ChallengeId, "000000")).Outcome);
        }

        Assert.Equal(EmailCodeOutcome.Expired, (await ValidateSignInAsync(recipient.Email, issued.ChallengeId, "000000")).Outcome);
    }

    /// <summary>不能自助找回的账号收到一封不带码的告知信；挑战与没有账号时一样永远校验不过。</summary>
    [Fact]
    public async Task An_account_that_needs_an_administrator_gets_a_notice_instead_of_a_code()
    {
        const string email = "notice@example.test";
        var notice = new EmailCodeRecipient(Guid.NewGuid(), email, "stamp-1");
        var issued = await _store.IssuePasswordResetAsync(email, target: null, FixedRegistrationPolicy.Policy, notice);

        await _queue.RunAllAsync(_sender, _time);
        Assert.Empty(Assert.Single(_sender.Calls).Code);
        Assert.Null(await ValidatePasswordResetAsync(email, issued.ChallengeId, "000000"));
    }

    private Task<EmailCodeCheck> ValidateSignInAsync(string email, Guid challengeId, string code)
        => _store.ValidateTwoFactorSignInAsync(email, new EmailVerificationInputDto { ChallengeId = challengeId, Code = code });

    private Task<EmailCodeProof?> ValidatePasswordResetAsync(string email, Guid challengeId, string code)
        => _store.ValidatePasswordResetAsync(email, new EmailVerificationInputDto { ChallengeId = challengeId, Code = code });

    private async Task AssertPasswordResetTooFrequentAsync(string email, EmailCodeRecipient? target)
    {
        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => _store.IssuePasswordResetAsync(email, target, FixedRegistrationPolicy.Policy));
        Assert.Equal(AuthErrorCodes.EmailCodeSendTooFrequent, exception.Code);
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

#if (IncludeLocalization)
    /// <summary>收件人没设过语言：按当前文化渲染。</summary>
    private sealed class NoPreferences : IAccountPreferenceStore
    {
        public Task<AccountPreferencesOutputDto?> GetForCurrentUserAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<AccountPreferencesOutputDto?>(null);

        public Task<AccountPreferencesOutputDto?> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult<AccountPreferencesOutputDto?>(null);

        public Task<AccountPreferencesOutputDto> SetForCurrentUserAsync(
            Guid userId,
            string field,
            string? value,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>文案回落成键名：测试只关心信里的验证码。</summary>
    private sealed class KeyLocalizer : IStringLocalizer
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] => new(name, name);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

#endif
    /// <summary>记下入队的工作项，由测试决定何时执行：执行时像后台消费者一样给新的服务提供器。</summary>
    private sealed class CapturingQueue : IBackgroundTaskQueue
    {
        private readonly ConcurrentQueue<Func<IServiceProvider, CancellationToken, ValueTask>> _items = new();

        public IReadOnlyList<Func<IServiceProvider, CancellationToken, ValueTask>> Items => [.. _items];

        public ValueTask QueueAsync(Func<IServiceProvider, CancellationToken, ValueTask> workItem, CancellationToken cancellationToken = default)
        {
            _items.Enqueue(workItem);
            return ValueTask.CompletedTask;
        }

        public bool TryQueue(Func<IServiceProvider, CancellationToken, ValueTask> workItem)
        {
            _items.Enqueue(workItem);
            return true;
        }

        public async Task RunAllAsync(IEmailSender sender, TimeProvider time, CancellationToken stopping = default)
        {
            await using var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton(sender)
                .AddSingleton<IClock>(new UtcClockProvider(time))
#if (IncludeLocalization)
                .AddSingleton<IAccountPreferenceStore, NoPreferences>()
                .AddSingleton<IStringLocalizer, KeyLocalizer>()
#endif
                .AddTransient<AccountCodeMailer>()
                .BuildServiceProvider();
            while (_items.TryDequeue(out var item))
            {
                await item(services, stopping);
            }
        }
    }

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
