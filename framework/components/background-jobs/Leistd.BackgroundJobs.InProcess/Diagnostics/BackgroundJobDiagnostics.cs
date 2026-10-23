using System.Diagnostics;

namespace Leistd.BackgroundJobs.InProcess.Diagnostics;

internal static class BackgroundJobDiagnostics
{
    internal const string ActivitySourceName = "Leistd.BackgroundJobs";
    private static readonly ActivitySource Source = new(ActivitySourceName);

    internal static Activity StartActivity(string name, ActivityContext? parent = null)
    {
        if (Source.StartActivity(name, ActivityKind.Internal, parent ?? default) is { } listened)
        {
            return listened;
        }

        // 无监听者或采样拒绝时仍保留本地链路关联，手建 Activity 不通过该 Source 导出。
        var activity = new Activity(name);
        if (parent is { } context)
        {
            activity.SetParentId(context.TraceId, context.SpanId, context.TraceFlags);
            activity.TraceStateString = context.TraceState;
        }

        return activity.Start();
    }
}
