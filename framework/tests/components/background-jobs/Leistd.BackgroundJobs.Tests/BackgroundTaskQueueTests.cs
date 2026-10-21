using System.Diagnostics;
using Leistd.AmbientContext;
using Leistd.BackgroundJobs.InProcess;
using Leistd.BackgroundJobs.InProcess.Options;
using Leistd.BackgroundJobs.InProcess.Queues;
using Leistd.BackgroundJobs.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Leistd.Tracing.Constants;
using Leistd.Tracing.Abstractions;
using Leistd.Tracing;
using Leistd.Security;
using Xunit;

namespace Leistd.BackgroundJobs.Tests;

/// <summary>进程内队列：有界背压、入队时的上下文在执行时还原、单项失败不影响后续工作项。</summary>
public sealed class BackgroundTaskQueueTests
{
    private static ServiceProvider Build(
        int capacity = 16,
        IAmbientContext? ambient = null,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddInProcessBackgroundJobs();
        services.Configure<InProcessBackgroundJobOptions>(o => o.QueueCapacity = capacity);
        if (ambient is not null)
        {
            services.AddSingleton(ambient);
        }

        configure?.Invoke(services);

        return services.BuildServiceProvider();
    }

    private static BackgroundTaskQueueWorker Worker(IServiceProvider provider)
        => provider.GetServices<IHostedService>().OfType<BackgroundTaskQueueWorker>().Single();

    private static async Task DrainAsync(IServiceProvider provider)
    {
        var queue = provider.GetRequiredService<BackgroundTaskQueue>();
        while (queue.Reader.TryRead(out var item))
        {
            await Worker(provider).RunAsync(item, CancellationToken.None);
        }
    }

    /// <summary>满了就告诉调用方，而不是静默丢弃或无限堆积。</summary>
    [Fact]
    public void TryQueue_reports_a_full_queue()
    {
        using var provider = Build(capacity: 1);
        var queue = provider.GetRequiredService<IBackgroundTaskQueue>();

        Assert.True(queue.TryQueue((_, _) => ValueTask.CompletedTask));
        Assert.False(queue.TryQueue((_, _) => ValueTask.CompletedTask));
    }

    /// <summary>入队时捕获的上下文在执行时还原：后台任务读到的是入队请求的主体、租户与链路标识。</summary>
    /// <remarks>执行流是消费者自己的，不做还原的话任务在空上下文里运行，写下的数据归属与审计操作人都会丢失。</remarks>
    [Fact]
    public async Task The_context_captured_at_enqueue_is_restored_while_the_item_runs()
    {
        var ambient = new RecordingAmbientContext();
        using var provider = Build(ambient: ambient);
        var queue = provider.GetRequiredService<IBackgroundTaskQueue>();

        ambient.Current = "enqueued";
        string? seen = null;
        await queue.QueueAsync((_, _) =>
        {
            seen = ambient.Current;
            return ValueTask.CompletedTask;
        });
        ambient.Current = "later";

        await DrainAsync(provider);

        Assert.Equal("enqueued", seen);
        Assert.Equal("later", ambient.Current);
    }

    [Fact]
    public async Task A_failing_item_does_not_stop_the_next_one()
    {
        using var provider = Build();
        var queue = provider.GetRequiredService<IBackgroundTaskQueue>();
        var ran = false;

        await queue.QueueAsync((_, _) => throw new InvalidOperationException("boom"));
        await queue.QueueAsync((_, _) =>
        {
            ran = true;
            return ValueTask.CompletedTask;
        });

        await DrainAsync(provider);

        Assert.True(ran);
    }

    /// <summary>工作项接上入队时的链路：没有监听者（未接 OpenTelemetry）时也要接上，日志里的 TraceId 才能与请求串起来。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_item_continues_the_trace_it_was_queued_in(bool listened)
    {
        using var listener = listened ? ListenToBackgroundJobs() : null;
        using var provider = Build();
        var queue = provider.GetRequiredService<IBackgroundTaskQueue>();
        Activity? seen = null;

        using (var request = new Activity("request").Start())
        {
            await queue.QueueAsync((_, _) =>
            {
                seen = Activity.Current;
                return ValueTask.CompletedTask;
            });

            Activity.Current = null;
            await DrainAsync(provider);

            Assert.NotNull(seen);
            Assert.Equal(request.TraceId, seen.TraceId);
            Assert.Equal(request.SpanId, seen.ParentSpanId);
        }

        Assert.Equal(listened ? BackgroundTaskQueueWorker.ActivitySourceName : string.Empty, seen.Source.Name);
    }

    /// <summary>失败日志记在工作项自己的链路里：按这条日志的 TraceId 能找到入队的那次请求。</summary>
    [Fact]
    public async Task A_failure_is_logged_inside_the_item_trace()
    {
        var logged = new List<ActivityTraceId?>();
        using var provider = Build(configure: services =>
            services.AddSingleton<ILoggerProvider>(new TraceRecordingLoggerProvider(logged)));
        var queue = provider.GetRequiredService<IBackgroundTaskQueue>();

        using var request = new Activity("request").Start();
        await queue.QueueAsync((_, _) => throw new InvalidOperationException("boom"));
        Activity.Current = null;

        await DrainAsync(provider);

        Assert.Contains(request.TraceId, logged);
    }

    /// <summary>入队时没有链路就开新的根链路，不继承消费循环里残留的上下文；两项互不串链路，执行完还原消费者原来的 Activity。</summary>
    [Fact]
    public async Task Items_without_a_parent_start_their_own_root_trace()
    {
        using var provider = Build();
        var queue = provider.GetRequiredService<IBackgroundTaskQueue>();
        var seen = new List<Activity?>();

        Activity.Current = null;
        for (var i = 0; i < 2; i++)
        {
            await queue.QueueAsync((_, _) =>
            {
                seen.Add(Activity.Current);
                return ValueTask.CompletedTask;
            });
        }

        using var worker = new Activity("worker-loop").Start();
        await DrainAsync(provider);

        Assert.All(seen, activity =>
        {
            Assert.NotNull(activity);
            Assert.NotEqual(worker.TraceId, activity.TraceId);
            Assert.Equal(default, activity.ParentSpanId);
        });
        Assert.NotEqual(seen[0]!.TraceId, seen[1]!.TraceId);
        Assert.Same(worker, Activity.Current);
    }

    /// <summary>
    /// 入队时的业务关联标识（故意不同于 TraceId）带到工作项的日志上，失败日志也一样；
    /// 下一个工作项不串用它。
    /// </summary>
    [Fact]
    public async Task The_enqueuing_correlation_id_scopes_the_items_logs_including_the_failure()
    {
        var logged = new List<(string Message, object? Correlation)>();
        using var provider = Build(configure: services =>
        {
            services.AddLogging(logging => logging.AddProvider(new ScopeRecordingLoggerProvider(logged)));
            services.AddAmbientContext();
            services.AddCorrelationIdCore();
        });
        var queue = provider.GetRequiredService<IBackgroundTaskQueue>();
        var correlation = provider.GetRequiredService<ICorrelationIdProvider>();

        using (new Activity("request").Start())
        using (correlation.Change("order-123"))
        {
            await queue.QueueAsync((services, _) =>
            {
                services.GetRequiredService<ILogger<BackgroundTaskQueueTests>>().LogWarning("working");
                throw new InvalidOperationException("boom");
            });
        }

        Activity.Current = null;
        await queue.QueueAsync((services, _) =>
        {
            services.GetRequiredService<ILogger<BackgroundTaskQueueTests>>().LogWarning("next");
            return ValueTask.CompletedTask;
        });

        await DrainAsync(provider);

        Assert.Equal("order-123", logged.Single(entry => entry.Message == "working").Correlation);
        Assert.Equal("order-123", logged.Single(entry => entry.Message.StartsWith("A background work item failed")).Correlation);
        Assert.NotEqual("order-123", logged.Single(entry => entry.Message == "next").Correlation);
    }

    private sealed class ScopeRecordingLoggerProvider(List<(string Message, object? Correlation)> logged)
        : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider? _scopes;

        private void Record(string message, object? correlation)
        {
            lock (logged) { logged.Add((message, correlation)); }
        }

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose() { }

        private sealed class Logger(ScopeRecordingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
                => owner._scopes?.Push(state);

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                object? correlation = null;
                owner._scopes?.ForEachScope((scope, _) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object>> pairs)
                    {
                        foreach (var pair in pairs)
                        {
                            if (pair.Key == CorrelationIdConstants.LogKey)
                            {
                                correlation = pair.Value;
                            }
                        }
                    }
                }, (object?)null);

                owner.Record(formatter(state, exception), correlation);
            }
        }
    }

    private static ActivityListener ListenToBackgroundJobs()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BackgroundTaskQueueWorker.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class TraceRecordingLoggerProvider(List<ActivityTraceId?> logged) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(logged);

        public void Dispose() { }

        private sealed class Logger(List<ActivityTraceId?> logged) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    lock (logged) { logged.Add(Activity.Current?.TraceId); }
                }
            }
        }
    }

    private sealed class RecordingAmbientContext : IAmbientContext
    {
        public string? Current { get; set; }

        public IDisposable Begin(System.Security.Claims.ClaimsPrincipal principal, string? correlationId = null)
            => throw new NotSupportedException();

        public AmbientContextSnapshot Capture()
            => new(null, new Dictionary<Type, object?> { [typeof(string)] = Current });

        public IDisposable Restore(AmbientContextSnapshot snapshot)
        {
            var previous = Current;
            Current = (string?)snapshot.States[typeof(string)];
            return new Restorer(() => Current = previous);
        }

        private sealed class Restorer(Action restore) : IDisposable
        {
            public void Dispose() => restore();
        }
    }
}
