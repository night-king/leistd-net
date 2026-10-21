
namespace Leistd.UnitOfWork.Events;

/// <summary>工作单元未完成即结束时的事件参数。</summary>
public class UnitOfWorkFailedEventArgs(
    IUnitOfWork unitOfWork,
    Exception? exception,
    bool isRolledBack) : UnitOfWorkEventArgs(unitOfWork)
{
    /// <summary>导致未能完成的异常；无从得知时为 <see langword="null"/>。</summary>
    public Exception? Exception { get; } = exception;

    /// <summary>是否已显式回滚。</summary>
    public bool IsRolledBack { get; } = isRolledBack;
}
