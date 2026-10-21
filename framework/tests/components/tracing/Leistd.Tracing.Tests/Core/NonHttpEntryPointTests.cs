using System.Diagnostics;
using Leistd.AmbientContext;
using Leistd.Security;
using Leistd.Tracing.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Tracing.Tests.Core;

/// <summary>没有入站请求时的关联标识：环境上下文贡献者（Hub 调用、后台作业、消息消费）。</summary>
/// <remarks>
/// 链路追踪的价值恰恰在后台作业、消息消费者、Hub 调用里能续上标识。
/// 只测 HTTP 中间件等于只测了它最不容易出问题的那一半。
/// </remarks>
public class NonHttpEntryPointTests
{
    private static ServiceProvider Build(Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAmbientContext();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddCorrelationIdCore();
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Ambient_scope_establishes_a_correlation_id_where_there_was_none()
    {
        using var provider = Build();
        var correlation = provider.GetRequiredService<ICorrelationIdProvider>();

        Assert.Null(correlation.Get());

        using (provider.GetRequiredService<IAmbientContext>().Begin(new System.Security.Claims.ClaimsPrincipal()))
        {
            Assert.False(string.IsNullOrEmpty(correlation.Get()));
        }

        Assert.Null(correlation.Get());
    }

    // 调用方带来的标识（消息头、作业参数）必须被采纳，否则跨进程的链路会在这里断开。
    [Fact]
    public void Caller_supplied_correlation_id_is_adopted()
    {
        using var provider = Build();
        var correlation = provider.GetRequiredService<ICorrelationIdProvider>();
        const string Incoming = "0af7651916cd43dd8448eb211c80319c";

        using (provider.GetRequiredService<IAmbientContext>().Begin(
                   new System.Security.Claims.ClaimsPrincipal(), correlationId: Incoming))
        {
            Assert.Equal(Incoming, correlation.Get());
        }
    }

    [Fact]
    public void Captured_correlation_id_is_restored_even_when_worker_has_an_activity()
    {
        using var provider = Build();
        var ambient = provider.GetRequiredService<IAmbientContext>();
        var correlation = provider.GetRequiredService<ICorrelationIdProvider>();
        AmbientContextSnapshot snapshot;

        using (correlation.Change("queued-job_123"))
        {
            snapshot = ambient.Capture();
        }

        using var listener = ListenToEverything();
        using var activity = new ActivitySource(nameof(NonHttpEntryPointTests)).StartActivity("worker");
        Assert.NotNull(activity);

        using (ambient.Restore(snapshot))
        {
            Assert.Equal("queued-job_123", correlation.Get());
        }

        Assert.Equal(activity.TraceId.ToHexString(), correlation.Get());
    }

    // 调用方指定的值优先于 Activity：它是跨链路的业务关联（消息、重试、补偿），
    // 被当前链路的 TraceId 覆盖就在这一跳断开。
    [Fact]
    public void The_caller_supplied_value_wins_over_the_activity()
    {
        using var provider = Build();
        var correlation = provider.GetRequiredService<ICorrelationIdProvider>();

        using var listener = ListenToEverything();
        using var activity = new ActivitySource(nameof(NonHttpEntryPointTests)).StartActivity("job");
        Assert.NotNull(activity);

        using (provider.GetRequiredService<IAmbientContext>().Begin(
                   new System.Security.Claims.ClaimsPrincipal(), correlationId: "order-7d1c"))
        {
            Assert.Equal("order-7d1c", correlation.Get());
        }
    }

    // 没有指定值时沿用当前 Activity，不另造一个：日志与 APM 用同一个标识。
    [Fact]
    public void Without_a_caller_value_the_activity_trace_id_is_used()
    {
        using var provider = Build();
        var correlation = provider.GetRequiredService<ICorrelationIdProvider>();

        using var listener = ListenToEverything();
        using var activity = new ActivitySource(nameof(NonHttpEntryPointTests)).StartActivity("job");
        Assert.NotNull(activity);

        using (provider.GetRequiredService<IAmbientContext>().Begin(new System.Security.Claims.ClaimsPrincipal()))
        {
            Assert.Equal(activity.TraceId.ToHexString(), correlation.Get());
        }
    }

    private static ActivityListener ListenToEverything()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
