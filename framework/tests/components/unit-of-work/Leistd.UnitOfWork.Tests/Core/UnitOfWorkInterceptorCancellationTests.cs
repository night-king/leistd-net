using Leistd.DependencyInjection.DynamicProxy.Registration;
using Leistd.EventBus.EventHandlers;
using Leistd.EventBus.Events;
using Leistd.EventBus.Local;
using Leistd.UnitOfWork.Attributes;
using Leistd.UnitOfWork.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Leistd.UnitOfWork.Tests.Core;

/// <summary>声明式工作单元把方法收到的取消令牌交给提交：客户端断开时提交前的取消按调用方取消处理，不记成提交失败。</summary>
/// <remarks>
/// 拦截器若以默认令牌调用 <c>CompleteAsync</c>，BeforeCommit 处理器用请求令牌做 I/O、请求恰在此时中止时，
/// 取消异常对不上"调用方令牌已取消"的判定，被记成 Error "commit failed"——每次客户端断开都多一条假故障。
/// </remarks>
public sealed class UnitOfWorkInterceptorCancellationTests
{
    [Fact]
    public async Task A_caller_cancellation_during_before_commit_handlers_is_not_a_commit_failure()
    {
        var (provider, logs) = Build();
        await using var _ = provider;
        var request = provider.GetRequiredService<RequestAborted>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<IOrderService>().PlaceAsync(request.Source.Token));

        Assert.DoesNotContain(logs.GetSnapshot(), record => record.Level >= LogLevel.Warning);
        Assert.Contains(logs.GetSnapshot(), record =>
            record.Level == LogLevel.Debug && record.Message.Contains("cancelled by the caller", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Before_commit_handlers_receive_the_token_of_the_intercepted_method()
    {
        var (provider, _) = Build();
        await using var __ = provider;
        var observed = provider.GetRequiredService<RequestAborted>();
        using var source = new CancellationTokenSource();
        observed.CancelOnHandle = false;

        await provider.GetRequiredService<IOrderService>().PlaceAsync(source.Token);

        Assert.Equal(source.Token, observed.HandlerToken);
    }

    // 只认声明为 CancellationToken 的参数：object 载荷里装箱的令牌不改变提交行为
    [Fact]
    public async Task A_boxed_token_in_an_object_payload_is_not_the_commit_token()
    {
        var (provider, _) = Build();
        await using var __ = provider;
        var observed = provider.GetRequiredService<RequestAborted>();
        observed.CancelOnHandle = false;
        using var payload = new CancellationTokenSource();

        await provider.GetRequiredService<IOrderService>().PlaceWithPayloadAsync(payload.Token);

        Assert.Equal(CancellationToken.None, observed.HandlerToken);
    }

    [Fact]
    public async Task The_first_declared_token_is_used_when_a_method_has_several()
    {
        var (provider, _) = Build();
        await using var __ = provider;
        var observed = provider.GetRequiredService<RequestAborted>();
        observed.CancelOnHandle = false;
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();

        await provider.GetRequiredService<IOrderService>().PlaceWithTwoTokensAsync(first.Token, second.Token);

        Assert.Equal(first.Token, observed.HandlerToken);
    }

    private static (ServiceProvider Provider, FakeLogCollector Logs) Build()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug));
        services.AddFakeLogging();
        services.AddLocalEventBus();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUnitOfWork();
        services.AddSingleton<RequestAborted>();
        services.AddTransient<IEventHandler<OrderPlaced>, ReserveStockHandler>();
        services.AddTransient<IOrderService, OrderService>();
        var provider = (ServiceProvider)new DynamicProxyServiceRegistrationCallbackFactory().CreateServiceProvider(services);
        return (provider, provider.GetFakeLogCollector());
    }

    /// <summary>请求中止：处理器执行时客户端断开，请求令牌随即取消。</summary>
    public sealed class RequestAborted
    {
        public CancellationTokenSource Source { get; } = new();
        public bool CancelOnHandle { get; set; } = true;
        public CancellationToken HandlerToken { get; set; }
    }

    public sealed class OrderPlaced : LocalEvent;

    public interface IOrderService
    {
        Task PlaceAsync(CancellationToken cancellationToken);

        Task PlaceWithPayloadAsync(object payload);

        Task PlaceWithTwoTokensAsync(CancellationToken first, CancellationToken second);
    }

    public sealed class OrderService(IUnitOfWorkManager manager) : IOrderService
    {
        [UnitOfWork]
        public Task PlaceAsync(CancellationToken cancellationToken) => Place();

        [UnitOfWork]
        public Task PlaceWithPayloadAsync(object payload) => Place();

        [UnitOfWork]
        public Task PlaceWithTwoTokensAsync(CancellationToken first, CancellationToken second) => Place();

        private Task Place()
        {
            manager.Current!.AddPendingEvents([new OrderPlaced()]);
            return Task.CompletedTask;
        }
    }

    // 用请求令牌做 I/O 的 BeforeCommit 处理器（如经 IHttpContextAccessor 取到的 RequestAborted）
    [UnitOfWorkEventHandler(Phase = UnitOfWorkPhase.BeforeCommit)]
    public sealed class ReserveStockHandler(RequestAborted request) : IEventHandler<OrderPlaced>
    {
        public async Task HandleAsync(OrderPlaced @event, CancellationToken cancellationToken = default)
        {
            request.HandlerToken = cancellationToken;
            if (!request.CancelOnHandle) return;
            await request.Source.CancelAsync();
            request.Source.Token.ThrowIfCancellationRequested();
        }
    }
}
