namespace Leistd.UnitOfWork.Database;

/// <summary>由工作单元提交并释放的事务资源。</summary>
public interface ITransactionApi : IDisposable
{
    /// <summary>提交事务；该边界不接受调用方取消。</summary>
    /// <remarks>
    /// <c>CompleteAsync</c> 在调用本方法之前完成最后一次取消检查；本方法开始后不响应调用方取消。
    /// 需要限制提交耗时用命令超时（工作单元的 <c>Timeout</c> 选项）。
    /// </remarks>
    Task CommitAsync();
}
