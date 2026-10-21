namespace Leistd.Lock.Memory
{
    internal sealed class SemaphoreEntry(DateTimeOffset createdAt) : IDisposable
    {
        private readonly object _lifecycleLock = new();
        private int _leaseCount;
        private bool _retired;
        private int _disposed;

        internal SemaphoreSlim Semaphore { get; } = new(1, 1);

        private DateTimeOffset LastReleasedAt { get; set; } = createdAt;

        internal bool TryAcquireLease()
        {
            lock (_lifecycleLock)
            {
                if (_retired)
                    return false;

                _leaseCount++;
                return true;
            }
        }

        internal void AbandonLease()
        {
            lock (_lifecycleLock)
            {
                EnsureLeaseExists();
                _leaseCount--;
            }
        }

        // 本次是否真的放开了租约；没有活动租约时为 false。
        internal bool ReleaseLease(DateTimeOffset releasedAt)
        {
            lock (_lifecycleLock)
            {
                if (_leaseCount == 0)
                    return false;

                Semaphore.Release();
                LastReleasedAt = releasedAt;
                _leaseCount--;
                return true;
            }
        }

        internal bool TryRetire(DateTimeOffset now, TimeSpan maxIdleTime)
        {
            lock (_lifecycleLock)
            {
                if (_retired || _leaseCount != 0 || now - LastReleasedAt <= maxIdleTime)
                    return false;

                _retired = true;
                return true;
            }
        }

        private void EnsureLeaseExists()
        {
            if (_leaseCount == 0)
                throw new SynchronizationLockException("Cannot release a semaphore entry without an active lease.");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Semaphore.Dispose();
        }
    }
}
