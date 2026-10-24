using System.Diagnostics;
using Leistd.BackgroundJobs.InProcess.Recurring;
using Leistd.BackgroundJobs.Recurring;
using Leistd.BackgroundJobs.Tests.TestDoubles;
using Leistd.ExceptionHandling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Leistd.BackgroundJobs.Tests;

/// <summary>真实容器中的观察者配对、终态、隔离与链路；Activity 监听器的进程状态需独占验证。</summary>
[CollectionDefinition(nameof(RecurringJobRunObserverTests), DisableParallelization = true)]
[Collection(nameof(RecurringJobRunObserverTests))]
public sealed class RecurringJobRunObserverTests
{
    private static readonly TimeSpan FailSafe = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Observers_are_paired_in_order_and_complete_after_the_watermark()
    {
        await using var host = new ObserverTestHost();
        host.Store.Writing = () => host.Events.Enqueue("watermark");
        Assert.Equal(RecurringJobRunnerResult.Completed, await host.RunAsync());
        Assert.Equal(["first.started", "second.started", "job", "watermark", "first.completed", "second.completed"], host.Events);
        Assert.Equal(2, host.Completed.Count);
        foreach (var entry in host.Completed)
        {
            Assert.Equal(RecurringJobRunOutcome.Succeeded, entry.Result.Outcome);
            Assert.Null(entry.Result.Exception);
            Assert.Equal(RecurringJobScope.Cluster, entry.Run.Scope);
            Assert.Equal(host.Time.GetUtcNow(), entry.Run.StartedAt);
        }
        Assert.Empty(host.Logs.Collector.GetSnapshot());
    }

    [Theory]
    [InlineData("business")]
    [InlineData("timeout")]
    [InlineData("resolution")]
    [InlineData("watermark")]
    public async Task Execution_failures_report_the_original_exception_and_leave_the_watermark_open(string failureKind)
    {
        Exception failure = failureKind == "business" ? new BusinessException("Test:Failure", "The job failed.")
            : failureKind == "timeout" ? new TaskCanceledException("The request timed out.")
            : new InvalidOperationException("The dependency failed.");
        await using var host = new ObserverTestHost(configure: services =>
        {
            if (failureKind == "resolution") services.AddTransient<ObservedJob>(_ => throw failure);
        });
        if (failureKind == "watermark") host.Store.WriteFailure = failure;
        else if (failureKind != "resolution") host.Work = (_, _, _) => throw failure;
        Assert.Equal(RecurringJobRunnerResult.Failed, await host.RunAsync());
        foreach (var entry in host.Completed)
        {
            Assert.Equal(RecurringJobRunOutcome.Failed, entry.Result.Outcome);
            Assert.Same(failure, entry.Result.Exception);
            Assert.Equal(ActivityStatusCode.Error, entry.Activity!.Status);
        }
        Assert.Equal(2, host.Completed.Count);
        Assert.Null(await host.Store.GetLastCompletedSlotAsync(ObserverTestHost.Name));
        Assert.Equal(LogLevel.Error, Assert.Single(host.Logs.Collector.GetSnapshot()).Level);
    }

    /// <summary>取消信号不能掩盖任务或准入存储的真实故障，非取消异常必须保留并记 Error。</summary>
    [Theory]
    [InlineData("task", false)]
    [InlineData("read", false)]
    [InlineData("task", true)]
    [InlineData("read", true)]
    public async Task Non_cancellation_failures_remain_failed_after_a_cancellation_signal(string phase, bool lockLost)
    {
        using var stop = new CancellationTokenSource();
        await using var host = new ObserverTestHost();
        var failure = new InvalidOperationException("The dependency failed during cancellation.");
        void Cancel()
        {
            if (lockLost) host.Lock.Lost.Cancel();
            else stop.Cancel();
        }

        if (phase == "task")
        {
            host.Work = (_, _, _) => { Cancel(); throw failure; };
        }
        else
        {
            host.Store.Reading = Cancel;
            host.Store.ReadFailure = failure;
        }

        Assert.Equal(RecurringJobRunnerResult.Failed, await host.RunAsync(stop.Token));
        var log = Assert.Single(host.Logs.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.Same(failure, log.Exception);
        Assert.Equal(phase == "task" ? 2 : 0, host.Completed.Count);
        Assert.Equal(phase == "task" ? 1 : 0, host.Executions);
        Assert.All(host.Completed, entry =>
        {
            Assert.Equal(RecurringJobRunOutcome.Failed, entry.Result.Outcome);
            Assert.Same(failure, entry.Result.Exception);
            Assert.Equal(ActivityStatusCode.Error, entry.Activity!.Status);
        });
        if (phase == "read") Assert.Empty(host.Events);
        host.Store.ReadFailure = null;
        Assert.Null(await host.Store.GetLastCompletedSlotAsync(ObserverTestHost.Name));
    }

    [Theory]
    [InlineData(false, false, false, RecurringJobRunOutcome.LockLost)]
    [InlineData(false, true, false, RecurringJobRunOutcome.LockLost)]
    [InlineData(true, false, false, RecurringJobRunOutcome.Canceled)]
    [InlineData(true, true, false, RecurringJobRunOutcome.Canceled)]
    [InlineData(true, true, true, RecurringJobRunOutcome.Canceled)]
    public async Task Known_cancellation_prevents_watermark_writes_even_when_the_job_returns(
        bool stopping, bool returnsNormally, bool both, RecurringJobRunOutcome expected)
    {
        using var stop = new CancellationTokenSource();
        await using var host = new ObserverTestHost(defaultStore: true);
        OperationCanceledException? thrown = null;
        host.Work = (_, _, token) =>
        {
            if (stopping) stop.Cancel();
            if (!stopping || both) host.Lock.Lost.Cancel();
            if (!returnsNormally) throw thrown = new OperationCanceledException(token);
            return Task.CompletedTask;
        };
        await host.RunAsync(stop.Token);
        Assert.Equal(2, host.Completed.Count);
        foreach (var entry in host.Completed)
        {
            Assert.Equal(expected, entry.Result.Outcome);
            Assert.Equal(expected == RecurringJobRunOutcome.LockLost ? ActivityStatusCode.Error : ActivityStatusCode.Unset,
                entry.Activity!.Status);
            if (thrown is not null) Assert.Same(thrown, entry.Result.Exception);
            else Assert.IsAssignableFrom<OperationCanceledException>(entry.Result.Exception);
        }
        Assert.Null(await host.Provider.GetRequiredService<IRecurringJobStateStore>().GetLastCompletedSlotAsync(ObserverTestHost.Name));
        Assert.DoesNotContain(host.Logs.Collector.GetSnapshot(), r => r.Level == LogLevel.Error);
        Assert.Equal(expected == RecurringJobRunOutcome.LockLost ? 1 : 0,
            host.Logs.Collector.GetSnapshot().Count(r => r.Message.Contains("lost its cluster lock", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("locked")]
    [InlineData("completed")]
    [InlineData("switch-error")]
    [InlineData("read-error")]
    public async Task Skipped_or_unstarted_runs_do_not_resolve_or_notify_observers(string reason)
    {
        var resolutions = 0;
        await using var host = new ObserverTestHost(enabled: _ => reason == "switch-error"
            ? throw new InvalidOperationException("The switch failed.") : reason != "disabled", configure: services =>
            {
                services.AddScoped<IRecurringJobRunObserver>(_ => { resolutions++; throw new InvalidOperationException("Unexpected resolution."); });
            });
        if (reason == "locked") host.Lock.Available = false;
        if (reason == "completed") await host.Store.SetLastCompletedSlotAsync(ObserverTestHost.Name, host.Time.GetUtcNow());
        if (reason == "read-error") host.Store.ReadFailure = new InvalidOperationException("The read failed.");
        await host.RunAsync();
        Assert.Equal(0, resolutions);
        Assert.Empty(host.Completed);
        Assert.Equal(0, host.Executions);
        Assert.Equal(reason.EndsWith("error", StringComparison.Ordinal) ? 1 : 0,
            host.Logs.Collector.GetSnapshot().Count(r => r.Level == LogLevel.Error));
    }

    [Theory]
    [InlineData("started")]
    [InlineData("completed")]
    [InlineData("resolution")]
    public async Task Observer_failures_do_not_change_job_or_watermark_results(string phase)
    {
        await using var host = new ObserverTestHost(configure: services =>
        {
            if (phase == "resolution") services.AddScoped<IRecurringJobRunObserver>(_ => throw new InvalidOperationException("The observer factory failed."));
        });
        if (phase == "started") host.Started = (name, _, _) => name == "first" ? throw new InvalidOperationException("Started failed.") : Task.CompletedTask;
        if (phase == "completed") host.Completing = (name, _) => name == "first" ? throw new InvalidOperationException("Completed failed.") : Task.CompletedTask;
        Assert.Equal(RecurringJobRunnerResult.Completed, await host.RunAsync());
        Assert.Equal(1, host.Executions);
        Assert.NotNull(await host.Store.GetLastCompletedSlotAsync(ObserverTestHost.Name));
        Assert.Equal(phase == "resolution" ? 0 : 2, host.Completed.Count);
        var warning = Assert.Single(host.Logs.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, warning.Level);
        if (phase != "resolution") Assert.Contains(nameof(RecordingObserver), warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_started_pairs_only_notified_observers(bool throws)
    {
        using var stop = new CancellationTokenSource();
        await using var host = new ObserverTestHost();
        host.Started = (_, _, token) =>
        {
            stop.Cancel();
            if (throws) throw new OperationCanceledException(token);
            return Task.CompletedTask;
        };
        Assert.Equal(RecurringJobRunnerResult.Canceled, await host.RunAsync(stop.Token));
        Assert.Equal(["first.started", "first.completed"], host.Events);
        Assert.Equal(RecurringJobRunOutcome.Canceled, Assert.Single(host.Completed).Result.Outcome);
        Assert.Equal(0, host.Executions);
        Assert.Null(await host.Store.GetLastCompletedSlotAsync(ObserverTestHost.Name));
        Assert.Empty(host.Logs.Collector.GetSnapshot());
    }

    [Fact]
    public async Task Scoped_observers_share_job_state_and_isolate_overlapping_jobs()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new ObserverTestHost(RecurringJobScope.EveryInstance, configure: services =>
            services.AddRecurringJob<ObservedJob>("component.maintenance", ObserverTestHost.Schedule, RecurringJobScope.EveryInstance));
        host.Work = async (services, context, token) =>
        {
            services.GetRequiredService<ObserverRunState>().Value = context.JobName;
            if (context.JobName == ObserverTestHost.Name)
            {
                entered.SetResult();
                await release.Task.WaitAsync(FailSafe, token);
            }
        };
        var first = host.RunAsync();
        await entered.Task.WaitAsync(FailSafe);
        try { await host.RunAsync(name: "component.maintenance"); }
        finally { release.SetResult(); }
        await first.WaitAsync(FailSafe);
        Assert.Equal(4, host.Completed.Count);
        Assert.Equal(4, host.Completed.Select(c => c.Instance).Distinct().Count());
        Assert.All(host.Completed, c => Assert.Equal(c.Run.JobName, c.ScopedValue));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Activities_are_independent_and_restore_the_loop_context(int sampling)
    {
        using var listener = sampling < 0 ? null : new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Leistd.BackgroundJobs",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => sampling == 0 ? ActivitySamplingResult.None : ActivitySamplingResult.AllData
        };
        if (listener is not null) ActivitySource.AddActivityListener(listener);
        using var previous = new Activity("scheduler-loop").Start();
        await using var host = new ObserverTestHost(RecurringJobScope.EveryInstance);
        await host.RunAsync();
        await host.RunAsync();
        Assert.Same(previous, Activity.Current);
        var activities = host.Completed.Where(c => c.Name == "first").Select(c => c.Activity!).ToArray();
        Assert.Equal(2, activities.Length);
        Assert.NotEqual(activities[0].TraceId, activities[1].TraceId);
        foreach (var activity in activities)
        {
            Assert.Equal(default, activity.ParentSpanId);
            Assert.NotEqual(previous.TraceId, activity.TraceId);
            Assert.Equal(sampling == 1 ? "Leistd.BackgroundJobs" : string.Empty, activity.Source.Name);
            Assert.Equal("Succeeded", activity.GetTagItem("leistd.job.outcome"));
        }
        Assert.Same(host.JobActivity, activities[1]);
        Assert.Empty(host.Logs.Collector.GetSnapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Activity_context_is_restored_after_failure_or_skip(bool skip)
    {
        using var previous = new Activity("scheduler-loop").Start();
        await using var host = new ObserverTestHost(enabled: _ => !skip);
        host.Work = (_, _, _) => throw new InvalidOperationException("The job failed.");
        await host.RunAsync();
        Assert.Same(previous, Activity.Current);
        Assert.Equal(skip ? 0 : 2, host.Completed.Count);
    }

    [Fact]
    public async Task Duration_excludes_admission_and_completed_callbacks_and_success_stays_fixed()
    {
        using var stop = new CancellationTokenSource();
        await using var host = new ObserverTestHost(enabled: _ => true);
        host.Store.Reading = () => host.Time.Advance(TimeSpan.FromHours(2));
        host.Started = (_, _, _) => { host.Time.Advance(TimeSpan.FromSeconds(1)); return Task.CompletedTask; };
        host.Work = (_, _, _) => { host.Time.Advance(TimeSpan.FromSeconds(3)); return Task.CompletedTask; };
        host.Store.Writing = () => { host.Time.Advance(TimeSpan.FromSeconds(2)); stop.Cancel(); };
        host.Completing = (_, _) => { host.Time.Advance(TimeSpan.FromHours(1)); return Task.CompletedTask; };
        await host.RunAsync(stop.Token);
        Assert.Equal(2, host.Completed.Count);
        Assert.All(host.Completed, c =>
        {
            Assert.Equal(RecurringJobRunOutcome.Succeeded, c.Result.Outcome);
            Assert.Equal(TimeSpan.FromSeconds(7), c.Result.Duration);
            Assert.Equal(TimeSpan.FromSeconds(7), c.Result.CompletedAt - c.Run.StartedAt);
        });
    }

    [Fact]
    public async Task Every_instance_jobs_notify_in_each_service_provider_without_watermarks()
    {
        await using var first = new ObserverTestHost(RecurringJobScope.EveryInstance);
        await using var second = new ObserverTestHost(RecurringJobScope.EveryInstance);
        await Task.WhenAll(first.RunAsync(), second.RunAsync());
        foreach (var host in new[] { first, second })
        {
            Assert.Equal(2, host.Completed.Count);
            Assert.All(host.Completed, c => Assert.Equal(RecurringJobScope.EveryInstance, c.Run.Scope));
            Assert.Null(await host.Store.GetLastCompletedSlotAsync(ObserverTestHost.Name));
        }
    }

    [Theory]
    [InlineData(ServiceLifetime.Transient)]
    [InlineData(ServiceLifetime.Singleton)]
    public async Task Registered_observer_lifetimes_keep_the_same_instance_for_both_callbacks(ServiceLifetime lifetime)
    {
        var instances = new List<LifetimeObserver>();
        await using var host = new ObserverTestHost(RecurringJobScope.EveryInstance, configure: services =>
        {
            services.RemoveAll<IRecurringJobRunObserver>();
            services.Add(new ServiceDescriptor(typeof(IRecurringJobRunObserver), _ =>
            {
                var observer = new LifetimeObserver();
                instances.Add(observer);
                return observer;
            }, lifetime));
        });
        await host.RunAsync();
        await host.RunAsync();
        Assert.Equal(lifetime == ServiceLifetime.Singleton ? 1 : 2, instances.Count);
        Assert.All(instances, o => Assert.Equal(lifetime == ServiceLifetime.Singleton ? 4 : 2, o.Callbacks));
    }

    private sealed class LifetimeObserver : IRecurringJobRunObserver
    {
        private RecurringJobRun? started;
        internal int Callbacks { get; private set; }
        public Task OnRunStartedAsync(RecurringJobRun run, CancellationToken cancellationToken) { started = run; Callbacks++; return Task.CompletedTask; }
        public Task OnRunCompletedAsync(RecurringJobRun run, RecurringJobRunResult result) { Assert.Same(started, run); Callbacks++; return Task.CompletedTask; }
    }
}
