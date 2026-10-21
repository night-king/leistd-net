using System.Diagnostics;
using Leistd.Tracing.Services;
using Xunit;

namespace Leistd.Tracing.Tests.Core;

/// <summary>关联标识优先使用显式作用域；否则取当前 <see cref="Activity"/> 的 TraceId。</summary>
/// <remarks>
/// 默认情况下关联标识就是官方的 TraceId，不另造第二个；显式值用于跨链路的业务关联。
/// </remarks>
public class CorrelationIdTests
{
    private static Activity StartActivity()
    {
        // 必须有 Listener，否则 ActivitySource.StartActivity 返回 null（这正是"无 Activity"那一档）
        var source = new ActivitySource("Leistd.Tracing.Tests");
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Leistd.Tracing.Tests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        return source.StartActivity("test")!;
    }

    [Fact]
    public void Get_returns_the_ambient_activity_trace_id()
    {
        var provider = new CorrelationIdProvider();
        using var activity = StartActivity();

        Assert.NotNull(activity);
        Assert.Equal(activity.TraceId.ToHexString(), provider.Get());
    }

    [Fact]
    public void An_explicit_change_wins_over_the_ambient_activity()
    {
        var provider = new CorrelationIdProvider();
        using var activity = StartActivity();

        using (provider.Change("explicit-value"))
        {
            Assert.Equal("explicit-value", provider.Get());
        }

        Assert.Equal(activity.TraceId.ToHexString(), provider.Get());
    }

    [Fact]
    public void Change_restores_the_parent_value_and_is_idempotent()
    {
        var provider = new CorrelationIdProvider();

        var outer = provider.Change("outer");
        var inner = provider.Change("inner");

        Assert.Equal("inner", provider.Get());
        inner.Dispose();
        inner.Dispose();          // 幂等：重复释放不该把外层也弹掉
        Assert.Equal("outer", provider.Get());

        outer.Dispose();
        Assert.Null(provider.Get());
    }

    [Fact]
    public void Change_rejects_blank_values()
    {
        var provider = new CorrelationIdProvider();

        Assert.Throws<ArgumentException>(() => provider.Change("  "));
        Assert.Throws<ArgumentNullException>(() => provider.Change(null!));
    }
}
