using Leistd.Lock.Memory;
using Leistd.Lock.Memory.HostedServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leistd.Lock.Tests.Memory;

public sealed class MemoryLocalLockCleanupTests
{
    private const string Key = "cleanup-race";

    [Fact]
    public async Task CleanupDoesNotRemoveEntryWhileItHasAnActiveLease()
    {
        var timeProvider = new ManualTimeProvider();
        using var memoryLock = CreateLock(timeProvider);
        using var cleanup = CreateCleanup(memoryLock);

        await using (await memoryLock.LockAsync(Key))
        {
        }

        var originalEntry = memoryLock.Semaphores[Key];
        timeProvider.Advance(TimeSpan.FromMinutes(6));

        await using var heldLock = await memoryLock.LockAsync(Key);

        Assert.Equal(0, cleanup.CleanupOnce());
        Assert.Same(originalEntry, memoryLock.Semaphores[Key]);
        Assert.Null(await memoryLock.TryLockAsync(Key, TimeSpan.Zero));
    }

    [Fact]
    public async Task AcquisitionRetriesWhenRetiredEntryIsStillVisible()
    {
        var timeProvider = new ManualTimeProvider();
        using var memoryLock = CreateLock(timeProvider);

        await using (await memoryLock.LockAsync(Key))
        {
        }

        var retiredEntry = memoryLock.Semaphores[Key];
        timeProvider.Advance(TimeSpan.FromMinutes(6));
        Assert.True(retiredEntry.TryRetire(timeProvider.GetUtcNow(), TimeSpan.FromMinutes(5)));

        await using var handle = await memoryLock.LockAsync(Key);

        Assert.NotSame(retiredEntry, memoryLock.Semaphores[Key]);
        Assert.Null(await memoryLock.TryLockAsync(Key, TimeSpan.Zero));
    }

    [Fact]
    public async Task OldHandleDoesNotReleaseReplacementEntryAndIsIdempotent()
    {
        var timeProvider = new ManualTimeProvider();
        using var memoryLock = CreateLock(timeProvider);

        var oldHandle = await memoryLock.LockAsync(Key);
        var oldEntry = memoryLock.Semaphores[Key];
        Assert.True(memoryLock.Semaphores.TryRemove(
            new KeyValuePair<string, SemaphoreEntry>(Key, oldEntry)));

        await using var replacementHandle = await memoryLock.LockAsync(Key);

        await oldHandle.DisposeAsync();
        await oldHandle.DisposeAsync();

        Assert.Null(await memoryLock.TryLockAsync(Key, TimeSpan.Zero));
        oldEntry.Dispose();
    }

    [Fact]
    public async Task CleanupRemovesEntryOnlyAfterItBecomesIdleAgain()
    {
        var timeProvider = new ManualTimeProvider();
        using var memoryLock = CreateLock(timeProvider);
        using var cleanup = CreateCleanup(memoryLock);

        var handle = await memoryLock.LockAsync(Key);
        timeProvider.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(0, cleanup.CleanupOnce());

        await handle.DisposeAsync();
        Assert.Equal(0, cleanup.CleanupOnce());

        timeProvider.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, cleanup.CleanupOnce());
        Assert.False(memoryLock.Semaphores.ContainsKey(Key));
    }

    [Fact]
    public async Task TimedOutAndCanceledWaitersReleaseTheirEntryLeases()
    {
        var timeProvider = new ManualTimeProvider();
        using var memoryLock = CreateLock(timeProvider);
        using var cleanup = CreateCleanup(memoryLock);
        var heldLock = await memoryLock.LockAsync(Key);

        Assert.Null(await memoryLock.TryLockAsync(Key, TimeSpan.Zero));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => memoryLock.LockAsync(Key, new CancellationToken(canceled: true)));

        await heldLock.DisposeAsync();
        timeProvider.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(1, cleanup.CleanupOnce());
        Assert.False(memoryLock.Semaphores.ContainsKey(Key));
    }

    [Fact]
    public async Task StopAsyncAfterDisposeDoesNotThrow()
    {
        var timeProvider = new ManualTimeProvider();
        using var memoryLock = CreateLock(timeProvider);
        var cleanup = CreateCleanup(memoryLock, timeProvider);

        await cleanup.StartAsync(CancellationToken.None);
        cleanup.Dispose();

        var exception = await Record.ExceptionAsync(() => cleanup.StopAsync(CancellationToken.None));
        Assert.Null(exception);
    }

    [Fact]
    public async Task DisposeWaitsForInFlightCleanupCallback()
    {
        var timeProvider = new ManualTimeProvider();
        using var memoryLock = CreateLock(timeProvider);
        using var logger = new BlockingCleanupLogger();
        var cleanup = CreateCleanup(memoryLock, timeProvider, logger);

        await cleanup.StartAsync(CancellationToken.None);
        var callbackTask = timeProvider.Timer.FireAsync();
        Assert.True(logger.WaitUntilBlocked(TimeSpan.FromSeconds(5)));

        var disposeTask = Task.Run(cleanup.Dispose);
        Assert.True(timeProvider.Timer.WaitUntilDisposeStarted(TimeSpan.FromSeconds(5)));
        Assert.False(disposeTask.IsCompleted);

        logger.Release();
        await Task.WhenAll(callbackTask, disposeTask);
    }

    [Fact]
    public async Task CleanupAfterDisposeReturnsWithoutThrowing()
    {
        var timeProvider = new ManualTimeProvider();
        using var memoryLock = CreateLock(timeProvider);
        var cleanup = CreateCleanup(memoryLock, timeProvider);

        await cleanup.StartAsync(CancellationToken.None);
        cleanup.Dispose();

        Assert.Equal(0, cleanup.CleanupOnce());
    }

    private static MemoryLocalLock CreateLock(TimeProvider timeProvider) =>
        new(NullLogger<MemoryLocalLock>.Instance, timeProvider);

    private static MemoryLockCleanupHostedService CreateCleanup(
        MemoryLocalLock memoryLock,
        TimeProvider? timeProvider = null,
        ILogger<MemoryLockCleanupHostedService>? logger = null) =>
        timeProvider is null
            ? new(memoryLock, logger ?? NullLogger<MemoryLockCleanupHostedService>.Instance)
            : new(memoryLock, logger ?? NullLogger<MemoryLockCleanupHostedService>.Instance, timeProvider);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

        internal ManualTimer Timer { get; private set; } = null!;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            Timer = new ManualTimer(callback, state);
            return Timer;
        }

        internal void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private readonly object _lock = new();
        private readonly List<Task> _callbacks = [];
        private readonly ManualResetEventSlim _disposeStarted = new();
        private bool _disposed;

        internal Task FireAsync()
        {
            lock (_lock)
            {
                if (_disposed) return Task.CompletedTask;

                var callbackTask = Task.Run(() => callback(state));
                _callbacks.Add(callbackTask);
                return callbackTask;
            }
        }

        internal bool WaitUntilDisposeStarted(TimeSpan timeout) => _disposeStarted.Wait(timeout);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_lock)
            {
                return !_disposed;
            }
        }

        public ValueTask DisposeAsync()
        {
            Task callbacks;
            lock (_lock)
            {
                _disposed = true;
                callbacks = Task.WhenAll(_callbacks);
            }

            _disposeStarted.Set();
            return new ValueTask(callbacks);
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class BlockingCleanupLogger : ILogger<MemoryLockCleanupHostedService>, IDisposable
    {
        private readonly ManualResetEventSlim _blocked = new();
        private readonly ManualResetEventSlim _release = new();
        private int _hasBlocked;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Trace || Interlocked.Exchange(ref _hasBlocked, 1) != 0)
                return;

            _blocked.Set();
            _release.Wait();
        }

        internal bool WaitUntilBlocked(TimeSpan timeout) => _blocked.Wait(timeout);

        internal void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _blocked.Dispose();
            _release.Dispose();
        }
    }
}
