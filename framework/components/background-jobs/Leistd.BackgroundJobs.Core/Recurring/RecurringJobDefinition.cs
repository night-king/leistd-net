namespace Leistd.BackgroundJobs.Recurring;

/// <summary>一个登记过的周期任务，供调度器实现读取。</summary>
/// <remarks>由 <c>AddRecurringJob&lt;TJob&gt;</c> 生成；业务代码不直接构造。</remarks>
public sealed class RecurringJobDefinition
{
    internal RecurringJobDefinition(
        string name,
        Type jobType,
        RecurringJobScope scope,
        Func<IServiceProvider, RecurringJobSchedule> scheduleFactory,
        Func<IServiceProvider, bool>? isEnabled)
    {
        Name = name;
        JobType = jobType;
        Scope = scope;
        ScheduleFactory = scheduleFactory;
        IsEnabled = isEnabled;
    }

    /// <summary>任务名，全局唯一；也是集群锁与水位的键。</summary>
    public string Name { get; }

    /// <summary>实现 <see cref="IRecurringJob"/> 的任务类型。</summary>
    public Type JobType { get; }

    /// <summary>多副本下的执行范围。</summary>
    public RecurringJobScope Scope { get; }

    /// <summary>从容器取排期；调度器启动时调用一次，排期可以来自选项。</summary>
    public Func<IServiceProvider, RecurringJobSchedule> ScheduleFactory { get; }

    /// <summary>任务自身的开关；为 <see langword="null"/> 时总是开启。</summary>
    /// <remarks>
    /// 调度器每轮到点后、执行之前在本轮执行的作用域里求值，任务实例也从这个作用域解析：
    /// 开关与任务读同一份 <c>IOptionsSnapshot</c> 时，看到的是同一份配置。关闭时本轮不执行、不记完成。
    /// 求值应当同步、快速、无副作用；抛出异常按本轮失败处理，不当成关闭。
    /// </remarks>
    public Func<IServiceProvider, bool>? IsEnabled { get; }
}
