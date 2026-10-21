namespace Leistd.BackgroundJobs.Recurring;

/// <summary>多副本部署下，一个时段里由谁执行；登记时必须显式选择。</summary>
public enum RecurringJobScope
{
    /// <summary>全集群每个时段只执行一次：抢分布式锁（抢不到即跳过）并比对水位。用于作用在共享数据上的任务。</summary>
    Cluster = 1,

    /// <summary>每个副本各自执行，不加锁、不记水位。用于刷新本进程内状态的任务。</summary>
    EveryInstance = 2
}
