using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using Leistd.EventBus.Abstractions;
using Leistd.EventBus.Events;
using Leistd.ExceptionHandling;
using Leistd.Lock.Memory;
using Leistd.Settings.Definitions;
using Leistd.Settings.Events;
using Leistd.Settings.Management;
using Leistd.Settings.Resolution;
using Leistd.Settings.Tests.TestDoubles;
using Leistd.Settings.Validation;
using Leistd.TestBase.Doubles;
using Leistd.UnitOfWork.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Settings.Tests.Core;

/// <summary>默认写入入口按租户串行：取锁、独立事务、锁内校验与写入、提交后才释放锁。</summary>
/// <remarks>
/// 跨设置的校验器（未读保留天数不短于已读）读另一项设置后比较；两次写入若都在对方提交前完成校验，
/// 会各自用旧值通过，最终留下违序的一组。这里用真实的进程内锁与不连库的工作单元替身记录先后，
/// 第一方挂在校验之后，由锁装饰器的等待信号或第二方的校验完成信号（先到者）决定何时放行，不靠固定延时。
/// </remarks>
public sealed class SettingWriteSerializationTests : IDisposable
{
    private const string ReadDays = "Retention.ReadDays";
    private const string UnreadDays = "Retention.UnreadDays";
    private const string OrderCode = "Test:RetentionOrder";
    private const string HostLockKey = "settings:host";

    // 只防卡死；正常路径的每一步都由信号推进
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private static readonly AsyncLocal<string?> Party = new();

    private readonly ConcurrentQueue<string> _journal = new();
    private readonly MemoryLocalLock _memoryLock = new(NullLogger<MemoryLocalLock>.Instance);
    private readonly SettingDefinitionManager _definitions = new([new RetentionDefinitions()]);
    private readonly JournalingLock _lock;
    private readonly InMemorySettingStore _store;
    private readonly RecordingUnitOfWorkManager _unitsOfWork;

    public SettingWriteSerializationTests()
    {
        _lock = new JournalingLock(_memoryLock, Record);
        _store = new InMemorySettingStore(Record);
        _unitsOfWork = new RecordingUnitOfWorkManager(Record);
    }

    public void Dispose() => _memoryLock.Dispose();

    /// <summary>一次写入的完整顺序：取锁 → 独立事务 → 校验 → 写入 → 发布 → 提交 → 退出工作单元 → 释放锁。</summary>
    /// <remarks>提前释放锁（提交之前）或把发布挪到提交之后，都会让这里的顺序对不上。</remarks>
    [Fact]
    public async Task A_write_validates_stores_and_commits_inside_the_lock()
    {
        await Manager().SetAsync(ReadDays, "100", SettingScopes.Tenant);

        Assert.Equal(
            [
                $"lock-acquired:{HostLockKey}",
                "begin(requiresNew=True, transactional=True)",
                "validated(read=100, unread=365)",
                $"store:{ReadDays}=100",
                $"publish:{ReadDays}",
                "commit",
                "dispose",
                $"lock-release-start:{HostLockKey}",
                $"lock-released:{HostLockKey}",
            ],
            _journal);
    }

    /// <summary>宿主把默认工作单元配成非事务型时，写入仍开事务；隔离级别与超时沿用宿主默认值。</summary>
    [Fact]
    public async Task A_write_forces_a_transaction_but_keeps_the_host_isolation_and_timeout()
    {
        var defaults = Microsoft.Extensions.Options.Options.Create(new UnitOfWorkOptions
        {
            IsTransactional = false,
            IsolationLevel = IsolationLevel.Serializable,
            Timeout = TimeSpan.FromSeconds(30),
        });

        await Manager(unitOfWorkOptions: defaults).SetAsync(ReadDays, "100", SettingScopes.Tenant);

        var unitOfWork = Assert.Single(_unitsOfWork.Begun);
        Assert.True(unitOfWork.RequiresNew);
        Assert.True(unitOfWork.Options.IsTransactional);
        Assert.Equal(IsolationLevel.Serializable, unitOfWork.Options.IsolationLevel);
        Assert.Equal(TimeSpan.FromSeconds(30), unitOfWork.Options.Timeout);
        Assert.False(defaults.Value.IsTransactional);
    }

    /// <summary>第二方在锁外等待，第一方提交后才校验，读到第一方的新值并被违序规则拒绝。</summary>
    /// <remarks>
    /// 两组交错：写对写（已读 300 / 未读 200，初值 90 / 365），清除对写（未读 400 清回 365 / 已读 380）。
    /// 撤掉锁时第二方不经等待就用旧值通过校验，先到的信号变成校验完成，两次写入都成功并留下违序的一组。
    /// </remarks>
    [Theory]
    [InlineData(ReadDays, "300", UnreadDays, "200", null)]
    [InlineData(UnreadDays, null, ReadDays, "380", "400")]
    public async Task A_concurrent_write_waits_and_is_validated_against_the_committed_value(
        string firstName,
        string? firstValue,
        string secondName,
        string secondValue,
        string? presetUnread)
    {
        if (presetUnread is not null)
        {
            await _store.SetAsync(UnreadDays, presetUnread, SettingScopes.Tenant, userId: null);
        }

        var barrier = new FirstPartyBarrier();
        var secondWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _lock.Waiting += key =>
        {
            if (key == HostLockKey && Party.Value == "second")
            {
                secondWaiting.TrySetResult();
            }
        };

        Task? first = null;
        Task? second = null;
        Task observed;
        try
        {
            first = RunAs("first", () => Manager(barrier).SetAsync(firstName, firstValue, SettingScopes.Tenant));
            await Task.WhenAny(barrier.FirstValidated, first).WaitAsync(Guard);
            await (barrier.FirstValidated.IsCompleted ? Task.CompletedTask : first);

            second = RunAs("second", () => Manager(barrier).SetAsync(secondName, secondValue, SettingScopes.Tenant));
            observed = await Task.WhenAny(secondWaiting.Task, barrier.SecondValidated).WaitAsync(Guard);
        }
        finally
        {
            // 任何一步失败也先放行第一方，并有界地等两方结束，之后才释放测试锁；原始失败照常上抛
            barrier.ReleaseFirst();
            await DrainAsync(first, second);
        }

        var firstFailure = await Outcome(first!);
        var secondFailure = await Outcome(second!);

        Assert.True(observed == secondWaiting.Task, "The second write passed validation before waiting for the lock");
        Assert.Null(firstFailure);
        Assert.Equal(OrderCode, Assert.IsType<BusinessException>(secondFailure).Code);

        var journal = _journal.ToList();
        Assert.True(
            journal.IndexOf("first:commit") < journal.IndexOf($"first:lock-release-start:{HostLockKey}")
            && journal.IndexOf($"first:lock-release-start:{HostLockKey}") < journal.IndexOf($"second:lock-acquired:{HostLockKey}"),
            "The second write entered the lock before the first one committed and released it: " + string.Join(", ", journal));
        Assert.DoesNotContain("second:commit", journal);

        Assert.Equal(firstValue, _store.Get(firstName));
        Assert.Null(_store.Get(secondName));
        var read = int.Parse(_store.Get(ReadDays) ?? "90", CultureInfo.InvariantCulture);
        var unread = int.Parse(_store.Get(UnreadDays) ?? "365", CultureInfo.InvariantCulture);
        Assert.True(unread >= read, $"Stored retention is out of order: read={read}, unread={unread}");
    }

    /// <summary>校验或存储失败：本次回滚、不提交、不发布，锁随之释放，下一次写入可以立即进入。</summary>
    [Theory]
    [InlineData("validator")]
    [InlineData("store")]
    public async Task A_failed_write_rolls_back_and_releases_the_lock(string failingStep)
    {
        if (failingStep == "store")
        {
            _store.NextWriteFailure = new InvalidOperationException("store failed");
        }

        // 已读 400 天长于未读的 365 天，被规则拒绝；100 天合规，失败落在存储上
        var value = failingStep == "validator" ? "400" : "100";

        await Assert.ThrowsAnyAsync<Exception>(() => Manager().SetAsync(ReadDays, value, SettingScopes.Tenant));

        Assert.Contains("rollback", _journal);
        Assert.DoesNotContain("commit", _journal);
        Assert.DoesNotContain($"publish:{ReadDays}", _journal);
        Assert.Equal($"lock-released:{HostLockKey}", _journal.Last());
        await AssertLockIsFreeAsync();
    }

    /// <summary>调用方取消或持锁资格失效（租约续期失败）都中止锁内的校验，不写入、不提交，并释放锁。</summary>
    [Theory]
    [InlineData("caller")]
    [InlineData("lease")]
    public async Task Cancellation_inside_the_lock_aborts_the_write_and_releases_the_lock(string source)
    {
        using var caller = new CancellationTokenSource();
        var blocking = new BlockingValidator();

        var write = Manager(blocking).SetAsync(ReadDays, "100", SettingScopes.Tenant, cancellationToken: caller.Token);
        await blocking.Entered.WaitAsync(Guard);
        if (source == "caller")
        {
            await caller.CancelAsync();
        }
        else
        {
            _lock.LoseLease();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(Guard));

        Assert.DoesNotContain(_journal, entry => entry.StartsWith("store:", StringComparison.Ordinal));
        Assert.DoesNotContain("commit", _journal);
        Assert.Equal($"lock-released:{HostLockKey}", _journal.Last());
        await AssertLockIsFreeAsync();
    }

    private async Task AssertLockIsFreeAsync()
    {
        await using var handle = await _memoryLock.TryLockAsync(HostLockKey, TimeSpan.Zero);
        Assert.NotNull(handle);
    }

    private DefaultSettingManager Manager(
        ISettingValueValidator? extra = null,
        IOptions<UnitOfWorkOptions>? unitOfWorkOptions = null)
    {
        // 每个写入方一个作用域：读取器各自独立，只共享存储与锁
        var provider = new DefaultSettingProvider(_definitions, _store, new FakeCurrentUser());
        ISettingValueValidator[] validators = extra is null
            ? [new RetentionOrderValidator(provider, Record)]
            : [new RetentionOrderValidator(provider, Record), extra];
        return new DefaultSettingManager(
            _definitions,
            _store,
            provider,
            validators,
            _lock,
            _unitsOfWork,
            new JournalingEventBus(Record),
            unitOfWorkOptions: unitOfWorkOptions);
    }

    private void Record(string entry) => _journal.Enqueue(Party.Value is { } party ? $"{party}:{entry}" : entry);

    private static Task RunAs(string party, Func<Task> write) => Task.Run(async () =>
    {
        Party.Value = party;
        await write();
    });

    // 只等结束、不判结果：结果由调用方另行断言，这里吞掉的异常不会掩盖 try 块里的原始失败
    private static async Task DrainAsync(params Task?[] writes)
    {
        try
        {
            await Task.WhenAll(writes.OfType<Task>()).WaitAsync(Guard);
        }
        catch (Exception)
        {
        }
    }

    private static async Task<Exception?> Outcome(Task write)
    {
        try
        {
            await write.WaitAsync(Guard);
            return null;
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class RetentionDefinitions : ISettingDefinitionProvider
    {
        public void Define(ISettingDefinitionContext context)
        {
            context.Add(ReadDays, "90", SettingScopes.Tenant).AsInteger(1, 3650);
            context.Add(UnreadDays, "365", SettingScopes.Tenant).AsInteger(1, 3650);
        }
    }

    // 与模板的保留期规则同形：读另一项设置的生效值再比较
    private sealed class RetentionOrderValidator(ISettingProvider settings, Action<string> record) : ISettingValueValidator
    {
        public async Task ValidateAsync(SettingValueValidationContext context, CancellationToken cancellationToken = default)
        {
            var candidate = int.Parse(context.Value, CultureInfo.InvariantCulture);
            var (read, unread) = context.Definition.Name == ReadDays
                ? (candidate, await settings.GetAsync<int>(UnreadDays, cancellationToken))
                : (await settings.GetAsync<int>(ReadDays, cancellationToken), candidate);

            if (unread < read)
            {
                record($"rejected(read={read}, unread={unread})");
                throw new BusinessException(OrderCode, $"Unread retention {unread} is shorter than read retention {read}.");
            }

            record($"validated(read={read}, unread={unread})");
        }
    }

    // 排在规则之后：第一方通过校验后挂住，第二方通过校验时发信号
    private sealed class FirstPartyBarrier : ISettingValueValidator
    {
        private readonly TaskCompletionSource _firstValidated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondValidated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstValidated => _firstValidated.Task;

        public Task SecondValidated => _secondValidated.Task;

        public void ReleaseFirst() => _releaseFirst.TrySetResult();

        public async Task ValidateAsync(SettingValueValidationContext context, CancellationToken cancellationToken = default)
        {
            switch (Party.Value)
            {
                case "first":
                    _firstValidated.TrySetResult();
                    await _releaseFirst.Task.WaitAsync(Guard, cancellationToken);
                    break;
                case "second":
                    _secondValidated.TrySetResult();
                    break;
            }
        }
    }

    // 进入后一直等到取消：观察取消令牌是否真的传进了锁内
    private sealed class BlockingValidator : ISettingValueValidator
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public async Task ValidateAsync(SettingValueValidationContext context, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await Task.Delay(Guard, cancellationToken);
        }
    }

    private sealed class JournalingEventBus(Action<string> record) : ILocalEventBus
    {
        public Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
            where TEvent : IEvent
            => PublishAsync((IEvent)@event, cancellationToken);

        public Task PublishAsync(IEvent @event, CancellationToken cancellationToken = default)
        {
            record(@event is SettingChangedEvent changed ? $"publish:{changed.Name}" : "publish");
            return Task.CompletedTask;
        }
    }
}
