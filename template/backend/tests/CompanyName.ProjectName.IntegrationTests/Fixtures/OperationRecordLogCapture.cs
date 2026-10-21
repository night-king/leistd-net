#if (!IncludeOperationRecords)
using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace CompanyName.ProjectName.IntegrationTests.Fixtures;

// 观察真实 Serilog 输出，不替换记录器或写入适配。
internal sealed class OperationRecordLogCapture : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> entries = new();

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Properties.TryGetValue("SourceContext", out var category)
            && category is ScalarValue { Value: "Leistd.OperationRecords" })
            entries.Enqueue(logEvent);
    }

    public LogEvent[] Snapshot() => entries.ToArray();

    public static object? Field(LogEvent entry, string name) =>
        entry.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar ? scalar.Value : null;
}
#endif
