using System.Collections.Concurrent;
using System.Diagnostics;
using Leistd.BackgroundJobs.InProcess;
using Leistd.BackgroundJobs.InProcess.Recurring;
using Leistd.BackgroundJobs.Recurring;
using Leistd.Lock.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Leistd.BackgroundJobs.Tests.TestDoubles;

internal sealed class ObserverTestHost : IAsyncDisposable
{
    internal const string Name = "test.observed";
    internal static readonly RecurringJobSchedule Schedule = RecurringJobSchedule.Every(TimeSpan.FromMinutes(5));
    internal FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 23, 10, 7, 0, TimeSpan.Zero));
    internal FakeLogger<RecurringJobRunner> Logs { get; } = new();
    internal ObservableLock Lock { get; } = new();
    internal ObserverStore Store { get; } = new();
    internal ConcurrentQueue<string> Events { get; } = new();
    internal ConcurrentQueue<ObserverCompletion> Completed { get; } = new();
    internal ServiceProvider Provider { get; }
    internal Func<IServiceProvider, RecurringJobContext, CancellationToken, Task> Work { get; set; } = (_, _, _) => Task.CompletedTask;
    internal Func<string, RecurringJobRun, CancellationToken, Task>? Started { get; set; }
    internal Func<string, RecurringJobRunResult, Task>? Completing { get; set; }
    internal int Executions { get; private set; }
    internal Activity? JobActivity { get; private set; }

    internal ObserverTestHost(RecurringJobScope scope = RecurringJobScope.Cluster,
        Func<IServiceProvider, bool>? enabled = null, Action<IServiceCollection>? configure = null, bool defaultStore = false)
    {
        var services = new ServiceCollection().AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddInProcessBackgroundJobs()
            .AddRecurringJob<ObservedJob>(Name, Schedule, scope, enabled);
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton<ILogger<RecurringJobRunner>>(Logs);
        services.AddSingleton<IDistributedLock>(Lock);
        if (!defaultStore) services.Replace(ServiceDescriptor.Singleton<IRecurringJobStateStore>(Store));
        services.AddScoped<ObserverRunState>();
        services.AddTransient(sp => new ObservedJob(async (context, token) =>
        {
            Executions++;
            JobActivity = Activity.Current;
            Events.Enqueue("job");
            await Work(sp, context, token);
        }));
        services.AddScoped<IRecurringJobRunObserver>(sp => CreateObserver("first", sp));
        services.AddScoped<IRecurringJobRunObserver>(sp => CreateObserver("second", sp));
        configure?.Invoke(services);
        Provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private RecordingObserver CreateObserver(string name, IServiceProvider services)
        => new(name, this, services.GetRequiredService<ObserverRunState>());

    internal Task<RecurringJobRunnerResult> RunAsync(CancellationToken token = default, string name = Name)
    {
        var definition = Provider.GetServices<RecurringJobDefinition>().Single(d => d.Name == name);
        return Provider.GetRequiredService<RecurringJobRunner>().RunOnceAsync(definition, Schedule, token);
    }

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();
        Lock.Lost.Dispose();
    }
}

internal sealed class ObserverRunState
{
    internal string? Value { get; set; }
}

internal sealed record ObserverCompletion(string Name, Guid Instance, RecurringJobRun Run, RecurringJobRunResult Result,
    string? ScopedValue, Activity? Activity);

internal sealed class RecordingObserver(string name, ObserverTestHost host, ObserverRunState state) : IRecurringJobRunObserver
{
    private readonly Guid instance = Guid.NewGuid();
    private RecurringJobRun? startedRun;
    private Activity? startedActivity;

    public async Task OnRunStartedAsync(RecurringJobRun run, CancellationToken cancellationToken)
    {
        startedRun = run;
        startedActivity = Activity.Current;
        host.Events.Enqueue($"{name}.started");
        if (host.Started is { } started) await started(name, run, cancellationToken);
    }

    public async Task OnRunCompletedAsync(RecurringJobRun run, RecurringJobRunResult result)
    {
        if (!ReferenceEquals(startedRun, run) || !ReferenceEquals(startedActivity, Activity.Current))
            throw new InvalidOperationException("The observer lost its run or Activity between callbacks.");
        host.Events.Enqueue($"{name}.completed");
        host.Completed.Enqueue(new(name, instance, run, result, state.Value, Activity.Current));
        if (host.Completing is { } completing) await completing(name, result);
    }
}

internal sealed class ObservedJob(Func<RecurringJobContext, CancellationToken, Task> work) : IRecurringJob
{
    public Task ExecuteAsync(RecurringJobContext context, CancellationToken cancellationToken) => work(context, cancellationToken);
}

internal sealed class ObserverStore : IRecurringJobStateStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> slots = new();
    internal Exception? ReadFailure { get; set; }
    internal Exception? WriteFailure { get; set; }
    internal Action? Reading { get; set; }
    internal Action? Writing { get; set; }

    public Task<DateTimeOffset?> GetLastCompletedSlotAsync(string jobName, CancellationToken cancellationToken = default)
    {
        Reading?.Invoke();
        return ReadFailure is { } failure ? throw failure : Task.FromResult(slots.TryGetValue(jobName, out var slot) ? (DateTimeOffset?)slot : null);
    }

    public Task SetLastCompletedSlotAsync(string jobName, DateTimeOffset slot, CancellationToken cancellationToken = default)
    {
        if (WriteFailure is { } failure) throw failure;
        slots[jobName] = slot;
        Writing?.Invoke();
        return Task.CompletedTask;
    }
}

internal sealed class ObservableLock : IDistributedLock, ILockHandle
{
    internal CancellationTokenSource Lost { get; } = new();
    internal bool Available { get; set; } = true;
    public CancellationToken LockLost => Lost.Token;
    public Task<ILockHandle> LockAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<ILockHandle>(this);
    public Task<ILockHandle?> TryLockAsync(string key, TimeSpan timeout, CancellationToken cancellationToken = default)
        => Task.FromResult<ILockHandle?>(Available ? this : null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
