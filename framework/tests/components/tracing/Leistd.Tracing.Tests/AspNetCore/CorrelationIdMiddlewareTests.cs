using System.Diagnostics;
using Leistd.Tracing.AspNetCore;
using Leistd.Tracing.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Leistd.Tracing.Abstractions;

namespace Leistd.Tracing.Tests.AspNetCore;

/// <summary>中间件的取值顺序（合法入站头 → Activity → 新建）、入站校验与日志作用域。</summary>
public class CorrelationIdMiddlewareTests(CorrelationIdMiddlewareTests.HostFixture fixture)
    : IClassFixture<CorrelationIdMiddlewareTests.HostFixture>
{
    private readonly CapturedScopes _scopes = fixture.Scopes;

    private readonly System.Net.Http.HttpClient _client = fixture.Client;

    private async Task<(string Correlation, string TraceIdentifier, string ActivityTraceId)> CallAsync(
        string? headerValue, string? traceParent = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        if (headerValue is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", headerValue);
        }

        if (traceParent is not null)
        {
            request.Headers.TryAddWithoutValidation("traceparent", traceParent);
        }

        using var response = await _client.SendAsync(request);
        var parts = (await response.Content.ReadAsStringAsync()).Split('|');
        return (parts[0], parts[1], parts[2]);
    }

    /// <summary>
    /// 让宿主真正建立 <see cref="Activity"/>。没有 Listener 时 ASP.NET Core 不建 Activity——
    /// 那正是"未接入 OpenTelemetry"那一档，两档都要覆盖。
    /// </summary>
    private static ActivityListener ListenToAspNetCore()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    [Fact]
    public async Task A_well_formed_inbound_id_is_accepted()
    {
        var (correlation, _, _) = await CallAsync("0af7651916cd43dd8448eb211c80319c");

        Assert.Equal("0af7651916cd43dd8448eb211c80319c", correlation);
    }

    [Fact]
    public async Task A_custom_inbound_id_is_preserved()
    {
        var (correlation, _, _) = await CallAsync("request-ABC_123");

        Assert.Equal("request-ABC_123", correlation);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has\nnewline")]          // 日志注入
    [InlineData("has\rcarriage")]         // 响应头注入
    [InlineData("bad;semicolon")]
    [InlineData("中文")]
    public async Task A_malformed_inbound_id_is_discarded_and_replaced(string malformed)
    {
        var (correlation, _, _) = await CallAsync(malformed);

        Assert.NotEqual(malformed, correlation);
        // 请求不因畸形头失败，而是换成新生成的合规标识
        Assert.Equal(32, correlation.Length);
    }

    /// <summary>上限与操作记录的列宽一致：采信的值落库时不会被截断，按它仍能搜到日志。</summary>
    [Fact]
    public async Task The_length_limit_matches_the_stored_column()
    {
        var longest = new string('a', CorrelationIdConstants.MaxLength);
        var tooLong = new string('a', CorrelationIdConstants.MaxLength + 1);

        Assert.Equal(longest, (await CallAsync(longest)).Correlation);
        Assert.NotEqual(tooLong, (await CallAsync(tooLong)).Correlation);
    }

    /// <summary>
    /// 不改写 <c>TraceIdentifier</c>：错误响应的 <c>traceId</c> 保持官方的链路标识，
    /// 关联标识只进日志作用域与响应头。
    /// </summary>
    [Fact]
    public async Task TraceIdentifier_is_left_to_the_host()
    {
        var (correlation, traceIdentifier, _) = await CallAsync("request-ABC_123");

        Assert.Equal("request-ABC_123", correlation);
        Assert.NotEqual(correlation, traceIdentifier);
    }

    [Fact]
    public async Task A_missing_inbound_header_yields_a_generated_id()
    {
        var (correlation, _, activityTraceId) = await CallAsync(headerValue: null);

        Assert.Equal(string.Empty, activityTraceId);   // 无 Listener → 无 Activity
        Assert.Equal(32, correlation.Length);
    }

    /// <summary>
    /// 入站值优先于 Activity：上游显式指定的关联标识经出站转发传到下游时不能被丢掉，
    /// 否则跨多条链路的业务关联在第一跳就断了。链路追踪仍由 traceparent 负责。
    /// </summary>
    [Fact]
    public async Task A_well_formed_inbound_id_wins_over_the_activity()
    {
        using var listener = ListenToAspNetCore();
        const string Inbound = "order-7d1c_retry-2";

        var (correlation, _, activityTraceId) = await CallAsync(Inbound);

        Assert.NotEqual(string.Empty, activityTraceId);
        Assert.Equal(Inbound, correlation);
    }

    [Fact]
    public async Task Without_an_inbound_id_the_activity_trace_id_is_used()
    {
        using var listener = ListenToAspNetCore();
        const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";

        var (correlation, _, activityTraceId) =
            await CallAsync(headerValue: null, traceParent: $"00-{TraceId}-00f067aa0ba902b7-01");

        Assert.Equal(TraceId, activityTraceId);
        Assert.Equal(TraceId, correlation);
    }

    [Fact]
    public async Task The_correlation_id_is_written_to_the_log_scope()
    {
        _scopes.Clear();

        var (correlation, _, _) = await CallAsync("request-ABC_123");

        Assert.Equal(correlation, _scopes.Find(CorrelationIdConstants.LogKey));
    }

    internal sealed class CapturedScopes
    {
        private readonly List<IReadOnlyDictionary<string, object>> _states = [];

        public void Clear() { lock (_states) { _states.Clear(); } }

        public void Add(IReadOnlyDictionary<string, object> state)
        {
            lock (_states) { _states.Add(state); }
        }

        public object? Find(string key)
        {
            lock (_states)
            {
                return _states.FirstOrDefault(s => s.ContainsKey(key))?[key];
            }
        }
    }

    private sealed class ScopeCapturingLoggerProvider(CapturedScopes scopes) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ScopeCapturingLogger(scopes);

        public void Dispose() { }

        private sealed class ScopeCapturingLogger(CapturedScopes scopes) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                if (state is IEnumerable<KeyValuePair<string, object>> pairs)
                {
                    scopes.Add(pairs.ToDictionary(x => x.Key, x => x.Value));
                }

                return NullScope.Instance;
            }

            // 必须返回 false。ASP.NET Core 的 HostingApplicationDiagnostics 会据
            // "Hosting 的 logger 是否启用" 决定要不要为请求建 Activity——恒真会让**每个**请求
            // 都有 Activity，于是"无 Activity 那一档"的测试永远走不到。
            // 探针不该改变被测系统；这里只需要 BeginScope，而它不经过 IsEnabled。
            public bool IsEnabled(LogLevel logLevel) => false;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();

                public void Dispose() { }
            }
        }
    }

    /// <summary>本类用例共享的宿主：配置固定。捕获的日志作用域跨用例累积，读取它的用例先清空再断言。</summary>
    public sealed class HostFixture : IAsyncLifetime
    {
        public IHost Host { get; private set; } = default!;

        public System.Net.Http.HttpClient Client { get; private set; } = default!;

        internal CapturedScopes Scopes { get; } = new();

        public async Task InitializeAsync()
        {
            Host = await new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddCorrelationId();
                        services.AddSingleton(Scopes);
                        services.AddSingleton<ILoggerProvider>(new ScopeCapturingLoggerProvider(Scopes));
                    })
                    .Configure(app =>
                    {
                        app.UseCorrelationId();
                        app.Run(async context =>
                        {
                            var provider = context.RequestServices.GetRequiredService<ICorrelationIdProvider>();
                            // 同时回显三处，用于断言它们一致
                            var activityTraceId = Activity.Current?.TraceId.ToHexString() ?? "";
                            await context.Response.WriteAsync(
                                $"{provider.Get()}|{context.TraceIdentifier}|{activityTraceId}");
                        });
                    }))
                .StartAsync();

            Client = Host.GetTestClient();
        }

        public async Task DisposeAsync()
        {
            Client.Dispose();
            await Host.StopAsync();
            Host.Dispose();
        }
    }
}
