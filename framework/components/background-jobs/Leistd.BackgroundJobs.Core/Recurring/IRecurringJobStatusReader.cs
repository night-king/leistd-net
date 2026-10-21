namespace Leistd.BackgroundJobs.Recurring;

/// <summary>读取本进程登记的周期任务的实际状态，供作业监控使用。</summary>
/// <remarks>
/// <para>状态与调度器用的是同一份事实：总开关、停用名单与排期取调度器启动时定下的结果，任务自身的开关在读取时求值。
/// 它描述的是<b>调用方所在进程</b>：任务跑在别的进程（如独立 Worker）时，由那个进程读取或上报，框架不做跨进程汇总。</para>
/// <para>集群任务的完成水位来自共享存储，须在与调度器相同的宿主上下文里读取（不在租户作用域内调用）。
/// 开关求值或水位读取失败时整次读取抛出，不把失败显示成“已关闭”或“从未完成”。
/// 是否错过窗口由消费方按 <see cref="RecurringJobStatus.CurrentSlot"/>、<see cref="RecurringJobStatus.LastCompletedSlot"/>
/// 与自己的宽限判定：任务可能正在执行、在等启动抖动，或刚在本时段后重新开启。</para>
/// </remarks>
public interface IRecurringJobStatusReader
{
    /// <summary>按登记顺序返回全部周期任务的状态。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<RecurringJobStatus>> GetStatusesAsync(CancellationToken cancellationToken = default);
}
