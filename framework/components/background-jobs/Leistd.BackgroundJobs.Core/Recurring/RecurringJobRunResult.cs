namespace Leistd.BackgroundJobs.Recurring;

/// <summary>周期执行的固定终态；结束回调的异常或取消不会改变它。</summary>
/// <param name="Outcome">执行结果。</param>
/// <param name="CompletedAt">结束回调前的 UTC 时刻。</param>
/// <param name="Duration">单调计时的耗时，包含开始回调、任务与水位写入，不含结束回调。</param>
/// <param name="Exception">失败时必非空；取消或失锁时为捕获的取消异常（若有），否则为 null；成功时为 null。</param>
public sealed record RecurringJobRunResult(
    RecurringJobRunOutcome Outcome, DateTimeOffset CompletedAt, TimeSpan Duration, Exception? Exception);
