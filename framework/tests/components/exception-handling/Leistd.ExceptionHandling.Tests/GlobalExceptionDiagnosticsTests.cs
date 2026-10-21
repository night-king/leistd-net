using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Leistd.ExceptionHandling.AspNetCore;
using Leistd.ExceptionHandling.AspNetCore.Handlers;
using Leistd.ExceptionHandling.Descriptors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Leistd.ExceptionHandling.Tests;

/// <summary>经真实管道观察全部诊断通道：日志（全类别）、HandledException 诊断事件与请求指标。</summary>
/// <remarks>
/// 处理器成功处理的异常只由处理器按状态记一次日志；官方中间件的 Error 日志与 HandledException 事件不再出现，
/// 否则预期的 4xx 会以 Error 带异常（含消息）进日志、5xx 记两遍。5xx 的请求耗时指标仍带异常类型标签。
/// 只看处理器类别的日志发现不了中间件那一份，这里收集所有类别。
/// </remarks>
public sealed class GlobalExceptionDiagnosticsTests
{
    private const string MiddlewareCategory = "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware";
    private const string HandledExceptionEvent = "Microsoft.AspNetCore.Diagnostics.HandledException";
    private const string ErrorTypeTag = "error.type";
    private const string SecretText = "caller-supplied-text";

    [Theory]
    [InlineData("/mapped", HttpStatusCode.NotFound)]
    [InlineData("/validation", HttpStatusCode.BadRequest)]
    [InlineData("/business", HttpStatusCode.BadRequest)]
    public async Task Expected_failures_leave_no_official_error_diagnostics(string path, HttpStatusCode expected)
    {
        await using var probe = await DiagnosticsProbe.StartAsync();

        using var response = await probe.Client.GetAsync(path);
        var durationTags = await probe.SingleDurationTagsAsync();

        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain(probe.Logs, record => record.Category == MiddlewareCategory);
        Assert.DoesNotContain(probe.Logs, record => record.Level >= LogLevel.Error);
        Assert.DoesNotContain(probe.Logs, record => record.Message.Contains(SecretText, StringComparison.Ordinal));
        Assert.DoesNotContain(HandledExceptionEvent, probe.Events);
        Assert.False(durationTags.ContainsKey(ErrorTypeTag));
    }

    [Fact]
    public async Task Unmapped_exception_is_logged_once_and_keeps_its_metric_type()
    {
        await using var probe = await DiagnosticsProbe.StartAsync();

        using var response = await probe.Client.GetAsync("/unmapped");
        var durationTags = await probe.SingleDurationTagsAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var error = Assert.Single(probe.Logs, record => record.Level >= LogLevel.Error);
        Assert.Equal(typeof(BusinessExceptionHandler).FullName, error.Category);
        Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.DoesNotContain(HandledExceptionEvent, probe.Events);
        Assert.Equal(typeof(InvalidOperationException).FullName, durationTags[ErrorTypeTag]);

        // 官方异常计数在抑制分支之外，仍按一次记下
        var counted = Assert.Single(probe.Exceptions.GetMeasurementSnapshot());
        Assert.Equal(typeof(InvalidOperationException).FullName, counted.Tags[ErrorTypeTag]);
    }

    [Fact]
    public async Task Server_side_business_failure_is_logged_once()
    {
        await using var probe = await DiagnosticsProbe.StartAsync();

        using var response = await probe.Client.GetAsync("/business-unavailable");
        var durationTags = await probe.SingleDurationTagsAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var error = Assert.Single(probe.Logs, record => record.Level >= LogLevel.Error);
        Assert.Equal(typeof(BusinessExceptionHandler).FullName, error.Category);
        Assert.IsType<BusinessException>(error.Exception);
        Assert.Equal(typeof(BusinessException).FullName, durationTags[ErrorTypeTag]);
    }

    // 宿主（或其他中间件）已写的同名标签不覆盖
    [Fact]
    public async Task Existing_error_type_tag_is_not_overwritten()
    {
        await using var probe = await DiagnosticsProbe.StartAsync("/unmapped", "host-defined");

        using var response = await probe.Client.GetAsync("/unmapped");
        var durationTags = await probe.SingleDurationTagsAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("host-defined", durationTags[ErrorTypeTag]);
    }

    // 处理器放行的框架请求错误：状态取自异常，与原先一样不产生官方诊断
    [Fact]
    public async Task Framework_request_error_keeps_its_status_without_official_diagnostics()
    {
        await using var probe = await DiagnosticsProbe.StartAsync();

        using var response = await probe.Client.GetAsync("/bad-request");
        var durationTags = await probe.SingleDurationTagsAsync();

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.DoesNotContain(probe.Logs, record => record.Category == MiddlewareCategory);
        Assert.DoesNotContain(HandledExceptionEvent, probe.Events);
        Assert.False(durationTags.ContainsKey(ErrorTypeTag));
    }

    // 响应已开始时处理器无从介入，官方诊断照常
    [Fact]
    public async Task Exception_after_response_started_keeps_official_diagnostics()
    {
        await using var probe = await DiagnosticsProbe.StartAsync();

        try
        {
            using var response = await probe.Client.GetAsync("/started");
            await response.Content.ReadAsStringAsync();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            // 响应开始后的异常中止请求，客户端读取失败是预期的
        }

        Assert.Contains(probe.Logs, record => record.Category == MiddlewareCategory
            && record.Level == LogLevel.Error
            && record.Exception is InvalidOperationException);
    }

    private sealed class DiagnosticsProbe : IAsyncDisposable, IObserver<KeyValuePair<string, object?>>
    {
        private readonly List<string> _events = [];
        private IHost _host = null!;
        private IDisposable? _subscription;
        private MetricCollector<double> _duration = null!;

        public HttpClient Client { get; private set; } = null!;

        public MetricCollector<long> Exceptions { get; private set; } = null!;

        public IReadOnlyList<FakeLogRecord> Logs => _host.Services.GetFakeLogCollector().GetSnapshot();

        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_events)
                    return [.. _events];
            }
        }

        public static async Task<DiagnosticsProbe> StartAsync(string? presetPath = null, string? presetErrorType = null)
        {
            var probe = new DiagnosticsProbe();
            probe._host = await new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddFakeLogging();
                        services.AddGlobalExceptionHandler(options =>
                        {
                            options.MapCode("Order:Unavailable", StatusCodes.Status500InternalServerError);
                            // 映射的日志级别由宿主给定；这里按预期失败给 Warning，只看中间件是否另记一份
                            options.MapException<NotFoundProbeException>(_ =>
                                new ExceptionDescriptor(StatusCodes.Status404NotFound, LogLevel: LogLevel.Warning));
                        });
                    })
                    .Configure(app =>
                    {
                        if (presetPath is not null)
                        {
                            app.Use(async (context, next) =>
                            {
                                if (context.Request.Path == presetPath)
                                    context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMetricsTagsFeature>()!
                                        .Tags.Add(new(ErrorTypeTag, presetErrorType));
                                await next(context);
                            });
                        }

                        app.UseGlobalExceptionHandler();
                        app.Run(async context =>
                        {
                            switch (context.Request.Path.Value)
                            {
                                case "/mapped":
                                    throw new NotFoundProbeException(SecretText);
                                case "/validation":
                                    throw new ValidationException(SecretText);
                                case "/business":
                                    throw new BusinessException("Order:Invalid", SecretText);
                                case "/business-unavailable":
                                    throw new BusinessException("Order:Unavailable", SecretText);
                                case "/bad-request":
                                    throw new BadHttpRequestException(SecretText, StatusCodes.Status413PayloadTooLarge);
                                case "/started":
                                    await context.Response.WriteAsync("partial");
                                    await context.Response.Body.FlushAsync();
                                    throw new InvalidOperationException(SecretText);
                                default:
                                    throw new InvalidOperationException(SecretText);
                            }
                        });
                    }))
                .StartAsync();

            // 每个宿主有自己的诊断源与计量工厂，并行用例互不串扰
            probe._subscription = probe._host.Services.GetRequiredService<DiagnosticListener>()
                .Subscribe(probe, _ => true);
            var meterFactory = probe._host.Services.GetRequiredService<IMeterFactory>();
            probe._duration = new MetricCollector<double>(
                meterFactory, "Microsoft.AspNetCore.Hosting", "http.server.request.duration");
            probe.Exceptions = new MetricCollector<long>(
                meterFactory, "Microsoft.AspNetCore.Diagnostics", "aspnetcore.diagnostics.exceptions");
            probe.Client = probe._host.GetTestClient();
            return probe;
        }

        // 耗时在请求收尾时记录，可能晚于客户端拿到响应
        public async Task<IReadOnlyDictionary<string, object?>> SingleDurationTagsAsync()
        {
            await _duration.WaitForMeasurementsAsync(1, TimeSpan.FromSeconds(10));
            return Assert.Single(_duration.GetMeasurementSnapshot()).Tags;
        }

        public async ValueTask DisposeAsync()
        {
            _subscription?.Dispose();
            _duration.Dispose();
            Exceptions.Dispose();
            await _host.StopAsync();
            _host.Dispose();
        }

        void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
        {
            lock (_events)
                _events.Add(value.Key);
        }

        void IObserver<KeyValuePair<string, object?>>.OnCompleted() { }

        void IObserver<KeyValuePair<string, object?>>.OnError(Exception error) { }
    }

    private sealed class NotFoundProbeException(string message) : Exception(message);
}
