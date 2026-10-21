namespace Leistd.BackgroundJobs.Options;

/// <summary>后台作业的运行开关，配置节 <c>Leistd:BackgroundJobs</c>。</summary>
public sealed class BackgroundJobOptions
{
    /// <summary>配置节路径。</summary>
    public const string SectionName = "Leistd:BackgroundJobs";

    /// <summary>是否在本进程执行周期任务，默认开启。</summary>
    /// <remarks>
    /// 一次性进程（迁移作业）与只想让部分副本跑任务的部署在这里关掉；关掉后任务不排期，队列照常工作。
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>在本进程停用的周期任务名。</summary>
    public IList<string> DisabledJobs { get; } = [];
}
