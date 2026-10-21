namespace Leistd.UnitOfWork.Database;

/// <summary>允许工作单元冲刷已登记数据库资源。</summary>
public interface ISupportsSavingChanges
{
    /// <summary>将待处理更改冲刷到数据库。</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
