# 实时通信

实时通信组件通过 SignalR 按资源向客户端推送业务事件，并提供订阅授权扩展点。

## 何时使用

| 场景 | 推荐 |
| --- | --- |
| 需要把"某资源发生了变更"实时推给正在关注它的客户端（按 `resourceKey` 订阅） | 引入 `Leistd.RealTime.AspNetCore.SignalR`，注入 `IBusinessEventPublisher` |
| 需要限制客户端只能订阅有权限的资源 | 实现并注册自定义 `IRealTimeSubscriptionAuthorizer`（无条件生效，没有开关） |
| 公共资源集中在固定前缀下（如 `public:`） | `AddPrefixRealTimeSubscriptions("public:")` |
| 仅编写业务代码（发布事件），不关心底层传输 | 只引用 `Leistd.RealTime.Core` 中的接口 |

实时事件不持久化。需要标题、内容、已读状态和历史记录时，使用 [通知组件](./notifications.md)。

## 安装

```bash
dotnet add package Leistd.RealTime.Core
dotnet add package Leistd.RealTime.AspNetCore.SignalR
```

## 注册

在 `Program.cs` 注册：

```csharp
builder.Services.AddRealTimeSignalR();

// 订阅授权必须显式选择，二者取一：
builder.Services.AddSingleton<IRealTimeSubscriptionAuthorizer, MyResourceAuthorizer>();
// 或者明确表示公共资源随便订阅：
builder.Services.AddAllowAllRealTimeSubscriptions();
```

未注册授权器时 `MapRealTimeHub()` 映射即失败，框架不提供默认授权器。

映射 Hub 端点（需登录）：

```csharp
app.MapRealTimeHub();
```

`AddRealTimeSignalR` 注册 SignalR 基座与事件发布，不注册授权器；`MapRealTimeHub(pattern = "/hubs/realtime")` 映射需登录的 Hub，授权器缺失时抛异常。
握手按 `HubIdentityOptions.PolicyName` 授权（未设置时按默认策略），与调用期复评同一策略；要换策略就设置该选项，不要在返回的构建器上追加 `RequireAuthorization`（只在握手时生效）。
授权器可以按 `Scoped`/`Transient` 注册并依赖作用域服务，每次 `Subscribe` 在 Hub 调用的作用域里解析。

心跳、超时、详细错误用 `AddSignalR(o => ...)` 配；解析 `UserIdentifier` 的 claim 顺序是 `ClaimTypeOptions.UserIds`（Security.Core），与框架其他组件读主体标识同一处配置。

## 使用

发布资源事件：

```csharp
public class ProductProfileService(IBusinessEventPublisher eventPublisher)
{
    public async Task UpdateProfileAsync(string ownerId, ProductProfileDto dto)
    {
        await eventPublisher.PublishToResourceAsync(
            resourceKey: $"product-profile:{ownerId}",
            eventName: "ProductProfileUpdated",
            @event: dto);
    }
}
```

客户端需先调用 Hub 的 `Subscribe("product-profile:{ownerId}")` 方法加入该资源分组，才能收到 `ProductProfileUpdated` 事件。

## 接口参考

| 成员 | 说明 |
| --- | --- |
| `IBusinessEventPublisher` | 业务事件推送器，面向"资源订阅"的细粒度实时推送，不感知通知领域模型 |
| `IBusinessEventPublisher.PublishToResourceAsync<TEvent>(resourceKey, eventName, @event, ct)` | 推送事件给订阅了指定资源的客户端；`TEvent : class` |
| `IRealTimeSubscriptionAuthorizer` | 实时资源订阅授权器 |
| `IRealTimeSubscriptionAuthorizer.AuthorizeAsync(context, ct)` | 判断当前用户是否允许订阅指定资源，返回 `bool` |
| `AddPrefixRealTimeSubscriptions(prefixes)` | 注册只放行指定前缀资源键的授权器（按序数比较）；与 `AddAllowAllRealTimeSubscriptions` 二选一，先注册者生效 |
| `RealTimeSubscriptionContext` | 订阅的 `ResourceKey` 和当前 `UserId`；租户等其它维度直接注入 `ICurrentTenant` 读取，Hub 调用内已由基座建立 |
| `AllowAllRealTimeSubscriptionAuthorizer : IRealTimeSubscriptionAuthorizer` | 始终返回 `true`；**不是默认注册**，经 `AddAllowAllRealTimeSubscriptions()` 显式选用 |
| `RealTimeHub : Hub` | 业务事件 Hub；提供 `Subscribe(resourceKey)` / `Unsubscribe(resourceKey)` 两个客户端可调用方法 |

## 实现行为

- Hub 只做资源订阅，不建任何用户分组：按用户寻址用 SignalR 自带的 `Clients.User(userId)`。
- `Subscribe` 无条件经过 `IRealTimeSubscriptionAuthorizer`，被拒绝时抛出 `HubException`；`Unsubscribe` 始终允许。
- 资源组名为 `resource:{resourceKey}`，不会自动拼租户；租户隔离必须由 `IRealTimeSubscriptionAuthorizer` 判定。推送失败只记录错误。

## 非 Hub 进程发布

独立的 Worker 也能经 `IBusinessEventPublisher` 推资源事件，送到连在 API 节点上的订阅者：注册 `AddRealTimeSignalR()` 与同一背板、同一通道前缀，不调用 `MapRealTimeHub()`，也不需要订阅授权器（订阅只发生在映射了 Hub 的节点上）。

```csharp
using StackExchange.Redis;

// Worker
builder.Services.AddRealTimeSignalR();
builder.Services.AddSignalR().AddStackExchangeRedis(redisConnectionString, options =>
    options.Configuration.ChannelPrefix = RedisChannel.Literal("my-app:production:"));
```

发布代码与在 API 里相同。背板、前缀、会话粘滞与失败语义见 [SignalR 基座](./aspnetcore-signalr.md#多实例部署)。

## 配置项

本组件没有自己的配置项：业务事件 Hub 路径在 `MapRealTimeHub(pattern)` 给出，默认 `/hubs/realtime`。

SignalR 传输层的配置不在本组件：心跳、超时、详细错误是 SignalR 的 `HubOptions`，用户标识解析在 [SignalR 基座](./aspnetcore-signalr.md)的 `HubIdentityOptions`。

## 注意事项

- 多副本部署或在 Worker 里发布必须配置 SignalR 背板，否则发布方所在节点之外的订阅者收不到事件，见 [SignalR 基座](./aspnetcore-signalr.md#多实例部署)与[非 Hub 进程发布](#非-hub-进程发布)。
- 用 Bearer 认证时，浏览器客户端只能把令牌放进查询串，宿主须在认证之前接入 SignalR 基座的 `UseHubAccessToken()`，见 [SignalR 基座](./aspnetcore-signalr.md#注册)。
- 本组件不提供在线状态查询；多实例在线状态需要宿主维护共享连接注册表。
- 订阅授权没有开关；`AddAllowAllRealTimeSubscriptions()` 是公共资源场景的显式选择。
- `PublishToResourceAsync` 推送失败只记日志、不抛异常，成功返回不代表订阅方一定收到。
- Hub 调用的环境上下文与有效性复评由 [SignalR 基座](./aspnetcore-signalr.md#有效性复评)提供：复评只在客户端调用 Hub 方法时发生，且不重新认证、只评策略要求。
  只有 Hub 所用策略里有按持久状态判定账号的 requirement 时，账号被禁用后既有连接才会在下一次 `Subscribe`/`Unsubscribe` 被中止；否则复评照常通过。
  在此之前已加入的资源组照常收到推送，只接收事件的连接不触发复评，直到断线重连（握手重新授权）或页面关闭。复评频率可用 `HubIdentityOptions.RevalidationInterval` 节流。

## 相关

- [通知](./notifications.md)
- [当前用户与身份信息](./security.md)
