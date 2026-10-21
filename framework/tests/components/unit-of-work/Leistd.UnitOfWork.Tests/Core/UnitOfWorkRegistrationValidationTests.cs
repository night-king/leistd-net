using Leistd.UnitOfWork.Attributes;
using Leistd.DependencyInjection.DynamicProxy.Registration;
using Leistd.EventBus.EventHandlers;
using Leistd.EventBus.Events;
using Leistd.EventBus.Local;
using Leistd.UnitOfWork.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.UnitOfWork.Tests.Core;

/// <summary>注册形式校验：声明失效必须让宿主起不来。</summary>
/// <remarks>
/// <para>阶段过滤靠动态代理实现，挂不上去是<b>完全静默</b>的：代码看起来是对的，
/// 过滤却根本不存在，处理器在两趟发布里各跑一次。因此<b>从描述符看得出来</b>的无法织入形态
/// （开放泛型）必须在构建容器时拦住——运行期没有任何可观察信号。</para>
/// <para>能织入的形式（含工厂委托的"同实例别名"）不该被拦，断言里各有一条正向用例。
/// 工厂委托背后的实现类型看不出来，属声明为不支持的形态，不在本组用例范围内。</para>
/// </remarks>
public sealed class UnitOfWorkRegistrationValidationTests
{
    /// <summary>工厂委托注册的事件处理器同样参与阶段过滤。</summary>
    /// <remarks>
    /// "同实例别名"是常见且正当的注册形态（一个实现只注册一次，接口做别名转发）：
    /// <c>services.AddSingleton&lt;IEventHandler&lt;T&gt;&gt;(sp =&gt; sp.GetRequiredService&lt;H&gt;())</c>。
    /// 织入只需 <c>ServiceType</c> 就能判定，特性运行期从 <c>invocation.TargetType</c> 取，
    /// 织入器本身也支持工厂型描述符——因此不该为了修双执行而禁掉它。
    /// </remarks>
    [Fact]
    public async Task Factory_registered_event_handler_still_runs_once()
    {
        var services = BaseServices();
        services.AddSingleton<CountingHandler>();
        services.AddSingleton<IEventHandler<ProbeEvent>>(sp => sp.GetRequiredService<CountingHandler>());

        await using var provider = (ServiceProvider)new DynamicProxyServiceRegistrationCallbackFactory()
            .CreateServiceProvider(services);

        var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true);
        uow.AddPendingEvents([new ProbeEvent()]);
        await uow.CompleteAsync();
        uow.Dispose();

        Assert.Equal(1, provider.GetRequiredService<CountingHandler>().Count);
    }

    /// <summary>
    /// 开放泛型注册同样参与不了阶段过滤；而且织入器会把描述符改写成工厂型，
    /// Microsoft DI 不接受"开放泛型服务类型 + 工厂委托"。两条都指向拒绝。
    /// </summary>
    [Fact]
    public void Open_generic_event_handler_registration_is_rejected()
    {
        var services = BaseServices();
        services.AddTransient(typeof(IEventHandler<>), typeof(OpenGenericHandler<>));

        var exception = Assert.Throws<InvalidOperationException>(Build(services));

        Assert.Contains("Open generic event handler", exception.Message);
    }

    [Fact]
    public void Type_registered_event_handler_is_accepted()
    {
        var services = BaseServices();
        services.AddTransient<IEventHandler<ProbeEvent>, AnnotatedHandler>();

        using var provider = (ServiceProvider)new DynamicProxyServiceRegistrationCallbackFactory()
            .CreateServiceProvider(services);

        Assert.NotNull(provider.GetService<IEventHandler<ProbeEvent>>());
    }

    /// <summary>带 <c>[UnitOfWork]</c> 的开放泛型实现被拒绝。</summary>
    /// <remarks>
    /// 开放泛型服务类型无法织入（织入器要把描述符改写成工厂型，而 Microsoft DI 不接受
    /// "开放泛型服务类型 + 工厂委托"），因此特性会静默失效。这一条从描述符就看得出来。
    /// </remarks>
    [Fact]
    public void Open_generic_unit_of_work_service_is_rejected()
    {
        var services = BaseServices();
        services.AddTransient(typeof(IGenericService<>), typeof(GenericServiceWithUnitOfWork<>));

        var exception = Assert.Throws<InvalidOperationException>(Build(services));

        Assert.Contains("open generic implementation", exception.Message);
    }

    /// <summary>有待发事件却没有 <c>ILocalEventDispatcher</c> 时立即失败，而不是静默丢弃。</summary>
    /// <remarks>
    /// 事件已经被登记，调用方有理由认为它会被发布。没有待发事件时不作要求——
    /// 工作单元可以独立使用，不强制安装事件总线。
    /// </remarks>
    [Fact]
    public async Task Pending_events_without_a_dispatcher_fail_instead_of_being_dropped()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUnitOfWork();   // 刻意不装本地事件总线
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        // 没有事件时照常完成
        using (var quiet = manager.Begin(requiresNew: true))
        {
            await quiet.CompleteAsync();
        }

        using var uow = manager.Begin(requiresNew: true);
        uow.AddPendingEvents([new ProbeEvent()]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => uow.CompleteAsync());
        Assert.Contains("pending event", exception.Message);
    }

    private static ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalEventBus();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUnitOfWork();
        return services;
    }

    private static Action Build(IServiceCollection services) =>
        () => new DynamicProxyServiceRegistrationCallbackFactory().CreateServiceProvider(services);

    public sealed class ProbeEvent : LocalEvent;

    [UnitOfWorkEventHandler(Phase = UnitOfWorkPhase.AfterCommit)]
    public sealed class AnnotatedHandler : IEventHandler<ProbeEvent>
    {
        public Task HandleAsync(ProbeEvent @event, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>无特性（等同 AfterCommit）且记执行次数，用于验证工厂注册仍只跑一次。</summary>
    public sealed class CountingHandler : IEventHandler<ProbeEvent>
    {
        public int Count { get; private set; }

        public Task HandleAsync(ProbeEvent @event, CancellationToken cancellationToken = default)
        {
            Count++;
            return Task.CompletedTask;
        }
    }

    public sealed class OpenGenericHandler<TEvent> : IEventHandler<TEvent>
        where TEvent : IEvent
    {
        public Task HandleAsync(TEvent @event, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }


    public interface IGenericService<T>
    {
        Task DoAsync(T value);
    }

    [UnitOfWork]
    public sealed class GenericServiceWithUnitOfWork<T> : IGenericService<T>
    {
        public Task DoAsync(T value) => Task.CompletedTask;
    }
}
