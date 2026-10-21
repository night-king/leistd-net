namespace Leistd.BackgroundJobs.Recurring;

/// <summary>表示宿主已注册周期任务的调度器实现。</summary>
/// <remarks>
/// 调度器实现（如 <c>AddInProcessBackgroundJobs()</c>）注册时登记本标记；缺失时 <c>AddRecurringJob</c> 注册的启动检查记录告警。
/// </remarks>
public sealed class RecurringJobSchedulerMarker;
