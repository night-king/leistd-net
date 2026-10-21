using System.Diagnostics;
using Leistd.AmbientContext;
using Leistd.Tracing.Abstractions;
using Leistd.Tracing.Constants;
using Microsoft.Extensions.Logging;

namespace Leistd.Tracing.AmbientContext;

// 非 HTTP 入口（Hub 调用、后台作业）的关联标识维度：指定值优先；有 Activity 时不切换（Get() 取其 TraceId）；
// 都没有时新建。与 HTTP 中间件一样同时打开日志作用域。
internal sealed class CorrelationIdAmbientContextContributor(
    ICorrelationIdProvider correlationIdProvider,
    ILoggerFactory? loggerFactory = null) : IAmbientContextContributor
{
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<CorrelationIdAmbientContextContributor>();

    public IDisposable? Enter(AmbientContextEnterContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.CorrelationId))
        {
            return Scope(context.CorrelationId, change: true);
        }

        return Activity.Current is null
            ? Scope(ActivityTraceId.CreateRandom().ToHexString(), change: true)
            : Scope(correlationIdProvider.Get(), change: false);
    }

    // 后台任务沿用入队时的关联标识，日志里两者才能按同一个标识串起来。
    public object? Capture() => correlationIdProvider.Get();

    public IDisposable? Restore(object? state)
        => state is string correlationId ? Scope(correlationId, change: true) : null;

    private IDisposable? Scope(string? correlationId, bool change)
    {
        if (string.IsNullOrEmpty(correlationId))
        {
            return null;
        }

        var changed = change ? correlationIdProvider.Change(correlationId) : null;
        var logScope = _logger?.BeginScope(new Dictionary<string, object> { [CorrelationIdConstants.LogKey] = correlationId });
        return changed is null && logScope is null ? null : new Both(logScope, changed);
    }

    // 先关日志作用域再还原关联标识，与打开顺序相反
    private sealed class Both(IDisposable? first, IDisposable? second) : IDisposable
    {
        public void Dispose()
        {
            first?.Dispose();
            second?.Dispose();
        }
    }
}
