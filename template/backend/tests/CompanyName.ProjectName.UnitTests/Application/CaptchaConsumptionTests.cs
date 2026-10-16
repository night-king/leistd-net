#if (LocalIdentity)
using CompanyName.ProjectName.Application.Auth.AppServices;
using CompanyName.ProjectName.Application.Auth.Captcha;
using CompanyName.ProjectName.Application.Auth.Policies;
using Leistd.Lock.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace CompanyName.ProjectName.UnitTests.Application;

/// <summary>图形验证码的「一次性」必须是并发意义上的一次。</summary>
/// <remarks>
/// <para><c>IDistributedCache</c> 没有原子的 get-and-delete，"读出来再删掉"之间存在窗口：
/// 同一个 token 并发提交时两个请求都会读到验证码、都判定通过——解一次验证码就能并发提交
/// 任意多次注册，正是验证码要挡的那件事。因此读/删/比必须在同一个临界区里。</para>
/// <para><b>时序由测试显式编排，超时只用来防挂死，不作为成功路径。</b>先启动第一次验证并等它
/// 真的<b>持有</b>锁，再启动第二次并等它对<b>同一个键</b>发起加锁，然后才放行第一次。
/// 用 <c>Task.Run</c> + <c>WhenAll</c> 或"等一个超时"是不够的：线程池一忙，第二个调用可能在
/// 第一个整套读删完之后才开始，那样即使生产代码没有锁也照样是"一真一假"。</para>
/// <para>缓存探针另外记录每次读/删发生时锁是否仍被持有——因此"把锁的作用域提前结束"
/// 这种变异也会确定性转红，而不只是"没调用过锁"才会红。</para>
/// </remarks>
public class CaptchaConsumptionTests
{
    /// <summary>只用于防止用例在实现有问题时永久挂住。走到超时即判失败。</summary>
    private static readonly TimeSpan FailSafe = TimeSpan.FromSeconds(5);

    /// <summary>当前调用的标识，随异步链流动。</summary>
    /// <remarks>
    /// 探针必须能判断「<b>本次</b>调用是否持锁」，而不只是「此刻有人持锁」。只看全局持锁计数
    /// 会漏掉最像真实维护失误的那种形态：锁只包住读取，删除落在锁外——那时第二个调用往往
    /// 已经抢到锁，探针于是把第一个调用的锁外删除误判为"在临界区内"。
    /// </remarks>
    private static readonly AsyncLocal<int> Invocation = new();

    [Fact]
    public async Task Concurrent_validation_of_one_token_succeeds_once_under_the_lock()
    {
        var (verifier, cache, captchaLock, token, code) = await CreateChallengeAsync();

        // 1) 第一次验证进入临界区后停在缓存读取上
        var first = ValidateAsync(verifier, token, code, invocation: 1);
        await WaitOrFailAsync(captchaLock.FirstAcquired, "第一次验证没有拿到锁");

        // 2) 第二次验证必须对同一个键发起加锁（此时被第一次挡住）
        var second = ValidateAsync(verifier, token, code, invocation: 2);
        await WaitOrFailAsync(captchaLock.SecondAttemptedSameKey, "第二次验证没有对同一个键加锁");
        // 那一刻锁仍应属于第一次调用——否则"竞争窗口成立"这个前提本身就不成立
        Assert.Equal(1, captchaLock.OwnerWhenSecondAttempted);

        // 3) 确认竞争窗口已经形成，才放行第一次
        cache.LetFirstReaderProceed();
        var results = await Task.WhenAll(first, second).WaitAsync(FailSafe);

        Assert.Equal(1, results.Count(ok => ok));
        Assert.Equal($"MyProject:Captcha:lock:{token}", Assert.Single(captchaLock.Attempts.Distinct()));
        Assert.Equal(2, captchaLock.Attempts.Count);
        // 读窗口从未重叠；且每次读/删都发生在**本次调用自己**持锁期间
        Assert.Equal(1, cache.MaxConcurrentReaders);
        Assert.Empty(cache.OperationsNotOwnedByCaller);
    }

    [Fact]
    public async Task Wrong_code_consumes_the_token_so_the_correct_one_fails_afterwards()
    {
        var (verifier, cache, _, token, code) = await CreateChallengeAsync();
        cache.LetFirstReaderProceed();   // 本用例不需要制造竞争

        Assert.False(await ValidateAsync(verifier, token, "wrong", invocation: 1));
        Assert.False(await ValidateAsync(verifier, token, code, invocation: 2));
    }

    /// <summary>在独立的异步流里标记调用标识，再执行验证。</summary>
    private static Task<bool> ValidateAsync(ICaptchaVerifier verifier, string token, string code, int invocation)
        => Task.Run(async () =>
        {
            // 在 Task.Run 内部赋值：每个任务因此拿到自己的那份，互不影响
            Invocation.Value = invocation;
            return await verifier.VerifyAsync(token, code);
        });

    private static async Task WaitOrFailAsync(Task signal, string message)
    {
        var completed = await Task.WhenAny(signal, Task.Delay(FailSafe));
        Assert.True(completed == signal, message);
    }

    /// <summary>发一张验证码，并把它的明文取出来——服务只回图片，明文在缓存里。</summary>
    private static async Task<(
        ICaptchaVerifier Verifier,
        ProbeCache Cache,
        GatedLock Lock,
        string Token,
        string Code)> CreateChallengeAsync()
    {
        var captchaLock = new GatedLock();
        var cache = new ProbeCache(captchaLock);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDistributedCache>(cache);
        services.AddSingleton<IDistributedLock>(captchaLock);
        // 注册策略现在按租户从设置里解析；本组用例只关心 token 的消费与加锁，
        // 给一个固定策略即可，不必把整条设置链拉进来。
        services.AddSingleton<IUserRegistrationPolicyProvider>(new FixedRegistrationPolicy());
        services.AddSingleton<ICaptchaAppService, CaptchaAppService>();
        services.AddSingleton<ICaptchaVerifier, CaptchaVerifier>();

        var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ICaptchaAppService>();
        var challenge = await service.GenerateCaptchaAsync();

        var code = await cache.GetStringAsync($"MyProject:Captcha:{challenge.CaptchaToken}");
        Assert.False(string.IsNullOrEmpty(code));

        // 发码阶段的读写不算，从这里开始观察
        cache.StartObserving();
        return (provider.GetRequiredService<ICaptchaVerifier>(), cache, captchaLock, challenge.CaptchaToken, code!);
    }

    /// <summary>按键互斥的进程内锁：记录尝试的键与<b>当前持有者</b>，并暴露两个编排信号。</summary>
    /// <remarks>
    /// 记 owner 而不只是"持锁计数"：探针要判断的是「本次调用是否持锁」。只看计数时，
    /// 第一个调用提前释放、第二个抢到锁之后，第一个在锁外做的删除会被误判为合规。
    /// </remarks>
    private sealed class GatedLock : IDistributedLock
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly List<string> _attempts = [];
        private readonly TaskCompletionSource _firstAcquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _owner;

        public Task FirstAcquired => _firstAcquired.Task;

        public Task SecondAttemptedSameKey => _secondAttempted.Task;

        /// <summary>第二次加锁尝试发生时，锁属于哪一次调用（0 表示无人持有）。</summary>
        public int OwnerWhenSecondAttempted { get; private set; }

        /// <summary>当前持有者的调用标识；0 表示无人持有。</summary>
        public int Owner => Volatile.Read(ref _owner);

        public IReadOnlyList<string> Attempts
        {
            get { lock (_attempts) { return [.. _attempts]; } }
        }

        public async Task<ILockHandle> LockAsync(string key, CancellationToken cancellationToken = default)
        {
            int attempt;
            lock (_attempts)
            {
                _attempts.Add(key);
                attempt = _attempts.Count;
            }

            // 第二次尝试的信号在 WaitAsync 之前发出：那一刻它正被第一次挡住，竞争窗口已成立
            if (attempt == 2 && _attempts[0] == key)
            {
                OwnerWhenSecondAttempted = Owner;
                _secondAttempted.TrySetResult();
            }

            await _gate.WaitAsync(cancellationToken);
            Volatile.Write(ref _owner, Invocation.Value);
            if (attempt == 1)
            {
                _firstAcquired.TrySetResult();
            }

            return new Handle(this);
        }

        public async Task<ILockHandle?> TryLockAsync(string key, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            lock (_attempts) { _attempts.Add(key); }
            if (!await _gate.WaitAsync(timeout, cancellationToken))
            {
                return null;
            }

            Volatile.Write(ref _owner, Invocation.Value);
            return new Handle(this);
        }

        private sealed class Handle(GatedLock owner) : ILockHandle
        {
            public CancellationToken LockLost => CancellationToken.None;

            public ValueTask DisposeAsync()
            {
                Volatile.Write(ref owner._owner, 0);
                owner._gate.Release();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>让第一个读取者停在读窗口里直到测试放行；同时记录读窗口并发数与"未持锁就操作"的次数。</summary>
    private sealed class ProbeCache(GatedLock captchaLock) : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _entries = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _proceed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _notOwned = [];
        private bool _observing;
        private int _concurrentReaders;

        public int MaxConcurrentReaders { get; private set; }

        /// <summary>观察期内「不是由本次调用持锁时」发生的读/删操作。</summary>
        public IReadOnlyList<string> OperationsNotOwnedByCaller
        {
            get { lock (_notOwned) { return [.. _notOwned]; } }
        }

        public void StartObserving() => _observing = true;

        public void LetFirstReaderProceed() => _proceed.TrySetResult();

        public byte[]? Get(string key)
        {
            Record("Get");
            lock (_entries) { return _entries.TryGetValue(key, out var value) ? value : null; }
        }

        public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            if (!_observing)
            {
                lock (_entries) { return _entries.TryGetValue(key, out var v) ? v : null; }
            }

            Record("GetAsync");
            var concurrent = Interlocked.Increment(ref _concurrentReaders);
            MaxConcurrentReaders = Math.Max(MaxConcurrentReaders, concurrent);
            try
            {
                // 停在读窗口里，由测试在确认竞争窗口成立后放行
                await _proceed.Task.WaitAsync(FailSafe, token);
                lock (_entries) { return _entries.TryGetValue(key, out var value) ? value : null; }
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentReaders);
            }
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            lock (_entries) { _entries[key] = value; }
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key) { }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key)
        {
            Record("Remove");
            lock (_entries) { _entries.Remove(key); }
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Record("RemoveAsync");
            lock (_entries) { _entries.Remove(key); }
            return Task.CompletedTask;
        }

        /// <summary>观察期内的每次读/删都必须发生在<b>本次调用自己</b>持锁期间。</summary>
        /// <remarks>
        /// 比对 owner 而不是"有没有人持锁"：锁只包住读取、删除落在锁外时，第二个调用往往
        /// 已经抢到锁，只看持锁计数会把这种失误判为合规。
        /// </remarks>
        private void Record(string operation)
        {
            if (!_observing)
            {
                return;
            }

            var caller = Invocation.Value;
            var owner = captchaLock.Owner;
            if (owner == caller && caller != 0)
            {
                return;
            }

            lock (_notOwned)
            {
                _notOwned.Add($"{operation}（调用 {caller}，锁属于 {owner}）");
            }
        }
    }

    /// <summary>固定的注册策略：本组用例不验策略解析，只需要一个确定的有效期。</summary>
    private sealed class FixedRegistrationPolicy : IUserRegistrationPolicyProvider
    {
        public Task<UserRegistrationPolicy> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new UserRegistrationPolicy
            {
                Enabled = true,
                CaptchaExpiryMinutes = 5,
#if (Email)
                EnableEmailVerification = false,
                EmailCodeExpiryMinutes = 5,
                EmailCodeSendIntervalSeconds = 60,
                EmailCodeMaxAttempts = 5,
#endif
            });
    }
}
#endif
