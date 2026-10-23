namespace Leistd.BackgroundJobs.Recurring;

/// <summary>观察周期任务的一次执行，包括任务解析与集群完成水位写入。</summary>
/// <remarks>
/// 在任务的作用域与 Activity 中按登记顺序回调；收到开始回调的实例必定收到一次结束回调，包括开始回调抛出的实例。
/// Scoped 实现可在字段中保存本次状态；Transient 每次执行只解析一次。Singleton 会被不同任务并发调用，
/// 必须线程安全，不得捕获 Scoped 服务或在字段中保存“当前执行”。回调异常被隔离并记录警告；
/// 集合解析失败时本次没有任何回调，任务继续执行。跳过或开始前的基础设施故障不回调。
/// </remarks>
public interface IRecurringJobRunObserver
{
    /// <summary>在任务实例解析前观察执行开始。</summary>
    /// <param name="run">本次执行的信息。</param>
    /// <param name="cancellationToken">停机或失锁时取消；取消后后续观察者与任务不再开始。</param>
    Task OnRunStartedAsync(RecurringJobRun run, CancellationToken cancellationToken);

    /// <summary>在终态确定后观察执行结束。</summary>
    /// <remarks>
    /// 不传入执行令牌，停机时仍尝试报告终态；实现应为 I/O 自设有限超时，慢回调会延长本轮执行与停机等待。
    /// 在本次任务作用域释放前调用；写宿主执行记录时应使用独立作用域或明确的宿主库上下文。
    /// </remarks>
    Task OnRunCompletedAsync(RecurringJobRun run, RecurringJobRunResult result);
}
