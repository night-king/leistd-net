using Leistd.BackgroundJobs.Options;

namespace Leistd.BackgroundJobs.InProcess.Options;

/// <summary>进程内实现的调优参数，默认配置节 <c>Leistd:BackgroundJobs:InProcess</c>。</summary>
public sealed class InProcessBackgroundJobOptions
{
    /// <summary>默认配置节路径。</summary>
    public const string SectionName = $"{BackgroundJobOptions.SectionName}:{SubsectionName}";

    // 指定了通用配置节时，本选项取其下的同名子节。
    internal const string SubsectionName = "InProcess";

    /// <summary>后台任务队列容量，默认 256；达到容量时生产者等待或入队失败。</summary>
    public int QueueCapacity { get; set; } = 256;
}
