using Leistd.BackgroundJobs.Recurring;

namespace Leistd.BackgroundJobs.Tests.TestDoubles;

/// <summary>记录每次执行的时段，供断言"执行了几次"。</summary>
internal sealed class CountingJob(JobLog log) : IRecurringJob
{
    public Task ExecuteAsync(RecurringJobContext context, CancellationToken cancellationToken)
    {
        log.Runs.Add(context);
        return Task.CompletedTask;
    }
}

/// <summary>每次执行都失败的任务。</summary>
internal sealed class FailingJob : IRecurringJob
{
    public Task ExecuteAsync(RecurringJobContext context, CancellationToken cancellationToken)
        => throw new InvalidOperationException("boom");
}

/// <summary>跨作用域共享的执行记录。</summary>
internal sealed class JobLog
{
    public List<RecurringJobContext> Runs { get; } = [];
}
