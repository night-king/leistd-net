namespace Leistd.BackgroundJobs.Recurring;

/// <summary>周期任务在本进程的实际调度状态。</summary>
/// <remarks>
/// 进程总开关、停用名单与排期在调度器启动时定下，任务自身的开关每次读取时求值。
/// 只有 <see cref="Active"/> 表示本进程会按排期执行它。
/// </remarks>
public enum RecurringJobScheduleState
{
    /// <summary>调度器尚未启动，本进程的调度事实还没确定。</summary>
    NotStarted,

    /// <summary>本进程关闭了周期任务（<c>BackgroundJobOptions.Enabled = false</c>）。</summary>
    DisabledInProcess,

    /// <summary>本进程的停用名单里有它（<c>BackgroundJobOptions.DisabledJobs</c>）。</summary>
    DisabledByName,

    /// <summary>排期在启动时取不出（排期工厂抛出异常），本进程不会执行它。</summary>
    InvalidSchedule,

    /// <summary>已按排期调度，但任务自身的开关此刻关着：到点也会跳过、不记完成。</summary>
    SwitchedOff,

    /// <summary>已按排期调度，开关开着。</summary>
    Active
}
