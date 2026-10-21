namespace Leistd.UnitOfWork;

/// <summary>使用 <see cref="AsyncLocal{T}"/> 隔离当前工作单元。</summary>
public class AmbientUnitOfWork : IAmbientUnitOfWork
{
    private readonly AsyncLocal<IUnitOfWork?> _currentUow = new();

    /// <inheritdoc />
    public IUnitOfWork? Get()
    {
        return _currentUow.Value;
    }

    /// <inheritdoc />
    public void Set(IUnitOfWork? unitOfWork)
    {
        _currentUow.Value = unitOfWork;
    }
}
