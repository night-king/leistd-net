namespace Leistd.UnitOfWork.Events;

/// <summary>工作单元事件的事件参数。</summary>
public class UnitOfWorkEventArgs(IUnitOfWork unitOfWork) : EventArgs
{
    /// <summary>触发本事件的工作单元。</summary>
    public IUnitOfWork UnitOfWork { get; } = unitOfWork;
}

