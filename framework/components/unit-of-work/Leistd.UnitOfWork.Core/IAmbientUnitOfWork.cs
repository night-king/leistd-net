namespace Leistd.UnitOfWork;

/// <summary>当前异步上下文中的工作单元。</summary>
public interface IAmbientUnitOfWork
{
    /// <summary>获取当前工作单元。</summary>
    IUnitOfWork? Get();

    /// <summary>设置当前工作单元。</summary>
    void Set(IUnitOfWork? value);
}
