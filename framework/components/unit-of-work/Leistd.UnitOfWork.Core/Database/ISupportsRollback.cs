namespace Leistd.UnitOfWork.Database;

/// <summary>允许工作单元回滚已登记资源。</summary>
public interface ISupportsRollback
{
    /// <summary>回滚资源的待提交变更。</summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
