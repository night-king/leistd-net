namespace Leistd.UnitOfWork.Events;

/// <summary>保存当前异步流的工作单元事件阶段。</summary>
public static class UnitOfWorkContext
{
    private static readonly AsyncLocal<UnitOfWorkPhase?> _currentPhase = new();

    /// <summary>当前工作单元阶段；不在提交流程中时为 <see langword="null"/>。</summary>
    public static UnitOfWorkPhase? CurrentPhase => _currentPhase.Value;

    // 进入一个阶段，释放时还原进入前的值：独立嵌套工作单元退出时须恢复外层阶段
    internal static IDisposable EnterPhase(UnitOfWorkPhase phase)
    {
        var previous = _currentPhase.Value;
        _currentPhase.Value = phase;
        return new PhaseScope(previous);
    }

    private sealed class PhaseScope(UnitOfWorkPhase? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _currentPhase.Value = previous;
            _disposed = true;
        }
    }
}
