using Leistd.BackgroundJobs.Recurring;

namespace Leistd.BackgroundJobs.InProcess.Recurring;

// 调度器启动时定下的调度事实：哪些任务排上了期、按什么排期，没排上的原因。
// 调度器与状态读取共用这一份，监控显示的就是实际执行的那一套，不按最新配置另算。
internal sealed class RecurringJobPlan
{
    private volatile IReadOnlyDictionary<string, PlannedJob>? _jobs;

    public IReadOnlyDictionary<string, PlannedJob>? Jobs => _jobs;

    public void Publish(IReadOnlyDictionary<string, PlannedJob> jobs) => _jobs = jobs;
}

// State 只取 DisabledInProcess、DisabledByName、InvalidSchedule、Active；SwitchedOff 由读取时的开关求值得出
internal sealed record PlannedJob(RecurringJobScheduleState State, RecurringJobSchedule? Schedule);
