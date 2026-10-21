namespace Leistd.BackgroundJobs.Recurring;

/// <summary>按固定排期重复执行的后台任务。</summary>
/// <remarks>
/// <para>用 <c>AddRecurringJob&lt;TJob&gt;(name, schedule, scope)</c> 登记；每次执行在新的依赖注入作用域里解析实例，
/// 可以注入 Scoped 服务。执行时没有请求主体与租户上下文：要逐个租户库处理时，在任务内部自行进入。</para>
/// <para>任务应当幂等：<see cref="RecurringJobScope.Cluster"/> 保证同一时段只成功执行一次，但中途失败后下一个时段会从头再来。</para>
/// </remarks>
public interface IRecurringJob
{
    /// <summary>执行一次。</summary>
    /// <param name="context">本次执行的时段信息。</param>
    /// <param name="cancellationToken">停机或失去集群锁时取消。</param>
    Task ExecuteAsync(RecurringJobContext context, CancellationToken cancellationToken);
}
