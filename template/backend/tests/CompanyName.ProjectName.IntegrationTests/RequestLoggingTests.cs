using System.Collections.Concurrent;
using Leistd.Tracing.Constants;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>请求完成日志走宿主的日志管道：与业务日志同一份配置里的格式、sink 与 enrich。</summary>
/// <remarks>
/// 宿主 logger 由容器独立持有、不替换静态 logger。请求日志中间件没有显式拿到宿主 logger 时，
/// 会写进启动期那个只输出纯文本的静态 logger——不报错，只是这一类事件不再结构化、也不带 enrich 的字段。
/// </remarks>
public sealed class RequestLoggingTests(ProjectWebApplicationFactory factory) : IClassFixture<ProjectWebApplicationFactory>
{
    [Fact]
    public async Task Request_completion_events_go_through_the_host_logging_pipeline()
    {
        var capture = new RequestCompletionCapture();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ILogEventSink>(capture)));
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);

        using var response = await client.GetAsync("/api/health/live");
        response.EnsureSuccessStatusCode();

        var completion = Assert.Single(capture.Events, e => Scalar(e, "RequestPath") as string == "/api/health/live");
        Assert.Equal(200, Scalar(completion, "StatusCode"));
        // 关联标识由请求外层的日志作用域提供：与响应头里回给调用方的是同一个
        Assert.Equal(
            Assert.Single(response.Headers.GetValues(CorrelationIdConstants.DefaultHeaderName)),
            Scalar(completion, CorrelationIdConstants.LogKey));
    }

    private static object? Scalar(LogEvent entry, string name) =>
        entry.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar ? scalar.Value : null;

    private sealed class RequestCompletionCapture : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> events = new();

        public IReadOnlyCollection<LogEvent> Events => events.ToArray();

        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Properties.TryGetValue("SourceContext", out var category)
                && category is ScalarValue { Value: "Serilog.AspNetCore.RequestLoggingMiddleware" })
                events.Enqueue(logEvent);
        }
    }
}
