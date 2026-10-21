# 事件总线

事件总线以发布/订阅解耦应用内部逻辑：发布方只声明「发生了什么」，订阅方实现 `IEventHandler<TEvent>` 各自响应、互不感知。`Leistd.EventBus.Local` 在发布方进程内同步消费。

## 何时使用

| 场景 | 推荐 |
| --- | --- |
| 单进程内解耦业务副作用（领域事件、应用内通知），发布方与处理器同进程 | `Leistd.EventBus.Local` |
| 仅编写业务代码（发布事件 / 实现处理器），不关心实现 | 只引用 `Leistd.EventBus.Core` 中的接口 |
| 需要跨进程/跨服务投递（消息队列） | 当前分组未提供，需另选分布式总线 |

默认情况下，发布方会等待所有处理器完成。活动工作单元中的本地事件则推迟到指定提交阶段。

## 安装

```bash
dotnet add package Leistd.EventBus.Core

dotnet add package Leistd.EventBus.Local
```

## 注册

在 `Program.cs` 注册本地事件总线：

```csharp
builder.Services.AddLocalEventBus();
```

`AddLocalEventBus` 以 Singleton 注册 `LocalEventBus` 并绑定到 `ILocalEventBus`，发布方注入它。`IEventBus` 只是各类总线的共同基接口，不注册为服务。可重复调用，不会重复注册。

事件处理器需自行注册（总线不做程序集扫描）。处理器在每次发布时通过独立 Scope 解析，因此 Scoped 注册可正常工作：

```csharp
builder.Services.AddScoped<IEventHandler<OrderPlacedEvent>, OrderPlacedHandler>();
```

## 使用

发布方注入 `ILocalEventBus`，发布一个事件对象：

```csharp
public class OrderNotifier(ILocalEventBus eventBus)
{
    public async Task NotifyPlacedAsync(string orderNo, CancellationToken ct = default)
    {
        await eventBus.PublishAsync(new OrderPlacedEvent { OrderNo = orderNo }, ct);
    }
}

public class OrderPlacedEvent : LocalEvent
{
    public string OrderNo { get; init; } = "";
}
```

DDD 项目中由聚合记录并随保存发布事件的组合方式见 [DDD 四层基座](../ddd-struct/ddd-struct.md)。

订阅方实现 `IEventHandler<TEvent>` 并注册到 DI：

```csharp
public class OrderPlacedHandler : IEventHandler<OrderPlacedEvent>
{
    public Task HandleAsync(OrderPlacedEvent @event, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
```

同一事件可注册多个处理器，发布时会依次全部执行。

## 接口参考

`Leistd.EventBus.Core` 包的 API 位于 `Leistd.EventBus.Abstractions`、`Leistd.EventBus.Events` 与 `Leistd.EventBus.EventHandlers`：

| 成员 | 说明 |
| --- | --- |
| `IEventBus` | 各类总线的共同基接口（发布方法的定义方）；不注册为服务，发布方注入具体的 `ILocalEventBus` |
| `IEventBus.PublishAsync<TEvent>(@event, ct)` | 泛型发布；按运行时类型解析处理器 |
| `IEventBus.PublishAsync(IEvent @event, ct)` | 非泛型发布，按事件运行时实际类型解析处理器 |
| `ILocalEventBus : IEventBus` | 进程内事件总线：提交后分发、不序列化；发布方注入它 |
| `IEventHandler<in TEvent>` | 事件处理器接口（`TEvent : IEvent`），实现 `HandleAsync` 订阅事件 |
| `IEvent` | 事件接口，含 `EventId`（Guid，用于幂等/追踪）与 `OccurredOn`（事件发生时间） |
| `ILocalEvent : IEvent` | 本地事件标记接口 |
| `BaseEvent : IEvent` | 事件抽象基类，构造时生成 `EventId`、设 `OccurredOn = DateTime.UtcNow`，标注 `[Serializable]` |
| `LocalEvent : BaseEvent, ILocalEvent` | 本地事件抽象基类，业务事件通常继承它；无参构造取系统当前时刻，`LocalEvent(occurredOn)` 用发布方给的时刻——发布方已从可替换的时间源（如 `IClock`）取了"现在"时用后者 |

## 实现行为

### Leistd.EventBus.Local（进程内本地总线）

- 每次发布在独立 Scope 中解析 `IEventHandler<TEvent>`，处理器可注册为 Scoped。
- 处理器按解析顺序串行 `await`（非并行），发布方等待全部完成，不是后台异步投递。
- 所有处理器都会执行；单个失败原样抛出，多个失败包装为 `AggregateException`，取消异常不参与聚合。
- 未解析到任何处理器时直接返回，不报错。
- 泛型与非泛型重载都按事件运行时类型解析处理器。

## 与工作单元的关系

`AddLocalEventBus` 除 `ILocalEventBus` 外还注册 `ILocalEventDispatcher`（同一个 `LocalEventBus` 实例）。两者分工：

| 接口 | 谁用 | 行为 |
| --- | --- | --- |
| `ILocalEventBus` | 业务代码 | 发布前先问 `ILocalEventDeferrer`；有活动事务边界则**推迟入队**，否则立即分发 |
| `ILocalEventDispatcher` | 实现事务边界的组件 | **绕过推迟**，立即分发。边界排空自己的待发队列时走这条 |
| `ILocalEventDeferrer` | 由事务边界组件**实现** | 契约在本包，实现在工作单元组件；未注册实现时总线一律立即分发 |

`ILocalEventDispatcher` 绕过推迟，避免工作单元排空事件时重新入队；处理器内新发布的事件仍会进入下一轮排空。

> 替换默认本地总线时，在 `AddLocalEventBus()` 之前注册自定义 `ILocalEventBus`，并同时提供语义一致的 `ILocalEventDispatcher`，否则工作单元排空仍走默认实现。工作单元在有待发事件却取不到 `ILocalEventDispatcher` 时抛出。

## 注意事项

- 处理器不会自动注册，必须显式注册 `IEventHandler<TEvent>`，否则发布时找不到处理器且不报错。
- 处理器耗时计入发布方的调用时长，长耗时副作用应转为后台任务。活动工作单元内的发布只入队并立即返回，处理器在工作单元完成时执行，见[与工作单元的关系](#与工作单元的关系)。
- 仅进程内有效，无跨进程/持久化能力；进程重启不保留未处理事件。
