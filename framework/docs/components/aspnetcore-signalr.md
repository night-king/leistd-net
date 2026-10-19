# SignalR 基座

解析 SignalR 连接主体的用户标识，为每次 Hub 调用建立环境上下文（主体、租户、链路标识），并复评宿主的授权策略。

## 何时使用

Hub 握手是一次 HTTP 请求，会走完整中间件管道；WebSocket 升级之后的方法调用不经中间件，主体、租户、链路标识与端点策略需由本包建立。

| 场景 | 推荐 |
| --- | --- |
| 宿主映射了任何 Hub，且 Hub 方法里要读当前用户、租户或写日志 | 引入本包 |
| 需要按用户寻址推送（`Clients.User(...)`） | 引入本包；用户标识按 `ClaimTypeOptions.UserIds` 解析 |
| 需要让账号禁用在已建立连接的下一次 Hub 方法调用上生效 | 引入本包，并在 Hub 所用策略里加入按持久状态判定账号的 requirement（复评不重新认证，见[有效性复评](#有效性复评)） |
| 用 JWT 等请求头认证，浏览器连接 Hub 只能把令牌放在查询串 | 在路由之后、认证之前 `app.UseHubAccessToken()` |
| 使用 `Leistd.RealTime` 或 `Leistd.Notifications` 的 SignalR 包 | 无需直接引入，它们已依赖本包 |

## 安装

```bash
dotnet add package Leistd.AspNetCore.SignalR
```

## 注册

```csharp
builder.Services.AddSignalRAmbientContext(); // 内含 AddSignalR() 与环境上下文
```

带选项：

```csharp
builder.Services.AddSignalRAmbientContext(options =>
{
    options.PolicyName = "HubAccess";                        // 默认用宿主的 DefaultPolicy
    options.RevalidationInterval = TimeSpan.FromSeconds(30); // 默认每次调用都复评
});
```

`AddSignalRAmbientContext` 幂等：realtime 与 notifications 同时安装时也只挂一份过滤器。

心跳、超时和详细错误仍通过 SignalR 的 `HubOptions` 配置：

```csharp
builder.Services.AddSignalR(options =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(10);
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});
```

用 JWT 等请求头认证时，浏览器的 WebSocket 与 SSE 连接设不了自定义请求头，SignalR 客户端只能把令牌放进查询串 `access_token`。在路由之后、认证之前接入 `UseHubAccessToken()`，它对宿主映射的全部 Hub 生效；用 Cookie 会话时不需要：

```csharp
using Leistd.AspNetCore.SignalR;

app.UseRouting();
app.UseHubAccessToken();
app.UseAuthentication();
```

## 使用

Hub 方法内可直接读环境态，与 HTTP 路径写法一致：

```csharp
using Leistd.Security.Users;
using Microsoft.AspNetCore.SignalR;

public class OrderHub(ICurrentUser currentUser) : Hub
{
    public Task<string> WhoAmI() =>
        Task.FromResult($"{currentUser.Id} @ {currentUser.TenantId?.ToString() ?? "host"}");
}
```

宿主为 HTTP 路径写的授权 handler 只读环境态（`ICurrentUser`、`ICurrentTenant`）与持久状态时，可原样在 Hub 上复评；
依赖 `HttpContext`、`AuthorizationHandlerContext.Resource` 或请求中间件写入的作用域数据的 handler 要自备回退路径，Hub 调用没有这些。在默认策略里加入 requirement：

```csharp
using Microsoft.AspNetCore.Authorization;

options.DefaultPolicy = new AuthorizationPolicyBuilder()
    .RequireAuthenticatedUser()
    .AddRequirements(new AccountStillActiveRequirement())   // 宿主自定义，按库里的账号状态判定；已建连接在下一次方法调用时复评
    .Build();
```

## 接口参考

| 成员 | 说明 |
| --- | --- |
| `AddSignalRAmbientContext(services, configure?)` | 注册 SignalR、`AmbientContextHubFilter` 与 `ClaimsSignalRUserIdProvider`；幂等 |
| `RequireHubAuthorization(hub)` | 让 Hub 握手按 `HubIdentityOptions.PolicyName`（未设置时为默认策略）授权，与调用期复评同一策略；组件的 `Map*Hub` 已调用，宿主自己映射的 Hub 用它代替 `RequireAuthorization` |
| `UseHubAccessToken(app)` | 只在 Hub 端点上把查询串 `access_token` 转成 Bearer 头并从查询串移除；按端点元数据识别 Hub，与映射路径无关；已带 `Authorization` 头时放行 |
| `AmbientContextHubFilter : IHubFilter` | 全局过滤器，覆盖方法调用、连接建立与断开 |
| `ClaimsSignalRUserIdProvider : IUserIdProvider` | 按 `ClaimTypeOptions.UserIds` 解析 SignalR `UserIdentifier`；只替换 SignalR 自带的 `DefaultUserIdProvider`，宿主已注册的实现保持不动，与注册顺序无关 |

## 实现行为

### 用户标识

- 按 `ClaimTypeOptions.UserIds` 顺序取第一个非空白 claim 作为 `Context.UserIdentifier`，供 `Clients.User(...)` 寻址。
- 读的是连接主体（`HubConnectionContext.User`）；业务代码仍用 `ICurrentUser`。

### 环境上下文

- `OnConnectedAsync`、`InvokeMethodAsync`、`OnDisconnectedAsync` 三处都在 `IAmbientContext.Begin(连接主体)` 的作用域内执行。
- 建立哪些维度取决于已注册的贡献者：主体（`Leistd.Security.Core`）、租户（`Leistd.MultiTenancy.AspNetCore`）、链路标识（`Leistd.Tracing.Core`）。未安装的维度就是没有，不会给猜测值。
- 连接主体未认证时照常建立上下文（链路标识与宿主自定义贡献者仍需要），但不做授权复评；主体为 `null` 时用空 `ClaimsPrincipal` 代入。

### 有效性复评

- 只在 `InvokeMethodAsync` 上复评；框架不主动断开已建立连接。复评失败之前（以及纯接收、从不调用方法的连接上），服务端推送照常送达，直到连接中止、断线或页面关闭；重连的握手按同一策略重新授权。
- 令牌到期关闭由 SignalR 的 `CloseOnAuthenticationExpiration` 决定：它只看认证票据或令牌的到期时间，不感知账号禁用或会话、令牌撤销。
- 在环境上下文之内执行，宿主为 HTTP 路径写的授权 handler（读 `ICurrentUser` 等环境态）可原样生效。
- 不通过时 `HubCallerContext.Abort()`，方法不执行，只记一条本过滤器的 Information；不抛异常，客户端以连接关闭结束这次调用（有值调用拿不到结果，流式调用收不到流项）。SignalR 会把 Hub 方法与过滤器抛出的任何异常（含 `HubException`）按 Error 记为调用失败（SignalR 调用分发器的 EventId 8，FailedInvokingHubMethod），而连接已中止，拒绝消息也送不到客户端。流式调用的空结果另有官方 Debug 日志。`Abort()` 关闭的连接不会被客户端的自动重连恢复，宿主前端需重新建立连接，握手按同一策略重新授权。
- 自己的 Hub 方法里，可预期的拒绝用返回值告知客户端，不抛 `HubException`：它控制的是公开给客户端的错误内容，不会降低服务端的日志级别。
- 节流状态存放在 `HubCallerContext.Items`，随连接生命周期。
- 宿主未注册 `IAuthorizationService` 时跳过复评。

## 配置项

`HubIdentityOptions`：

| 属性 | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `PolicyName` | `string?` | `null` | 握手（经 `RequireHubAuthorization`）与复评共用的策略名；`null` 时取 `IAuthorizationPolicyProvider.GetDefaultPolicyAsync()` |
| `RevalidationInterval` | `TimeSpan?` | `null` | 两次复评的最小间隔；`null` 表示每次调用都复评 |

## 多实例部署

没有背板时，`Clients.Group(...)` 与 `Clients.User(...)` 只到达连在本节点的客户端，跨节点的消息静默丢失。跑多副本必须配置背板；自托管时用官方 Redis 背板：

```csharp
using StackExchange.Redis;

builder.Services.AddSignalR().AddStackExchangeRedis(redisConnectionString, options =>
    options.Configuration.ChannelPrefix = RedisChannel.Literal("my-app:production:"));
```

宿主自行安装 `Microsoft.AspNetCore.SignalR.StackExchangeRedis`；本包不带背板 Provider，也不代为注册。背板与本包的注册顺序无关。

- **通道前缀按“应用 + 环境”区分**：Redis 的发布订阅不受数据库编号隔离，共用同一个 Redis 的应用或环境前缀相同就会互相收到消息。同一应用的所有进程（各 API 副本与下文的非 Hub 进程）必须用同一个前缀。
- **负载均衡要会话粘滞**：协商与后续请求必须落在同一个节点。只有全部客户端都只用 WebSocket 并在客户端选项里跳过协商时才可省略。亲和要配在实际选择节点的那一层（反向代理的 upstream、Ingress 控制器），经过 Kubernetes Service 的 ClientIP 亲和只在流量确实经过该 Service 且源地址稳定时有效。
- **Redis 不可用**：期间的消息丢失、不缓冲，恢复后自动重连，不补发。连接串带 `abortConnect=false`，背板的连接初始化不因 Redis 暂时不可达直接中止，之后由客户端自行重连；这只针对背板，缓存、锁等其他 Redis 依赖的调用照样可能失败。不要把推送成功当作业务提交的前提。
- Redis Cluster 在连接串里列出各节点即可，不需要改代码。

背板只解决跨节点路由。共享在线状态需宿主自建注册表；断线后的消息补拉需持久化，可使用[通知组件](./notifications.md)。

## 非 Hub 进程发布

只发消息、不接客户端连接的进程（如独立的后台作业 Worker）经同一背板发到连在其他节点上的客户端：注册 SignalR 与同一背板、同一前缀，不映射 Hub，用官方 `IHubContext<THub>` 发送。`THub` 必须与持有连接的节点映射的是同一个 Hub 类型：背板按 Hub 类型名划分通道。

```csharp
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

// Worker：通用宿主，不映射任何 Hub
builder.Services.AddSignalR().AddStackExchangeRedis(redisConnectionString, options =>
    options.Configuration.ChannelPrefix = RedisChannel.Literal("my-app:production:"));

// 发送方注入 IHubContext<OrderHub>，与 API 节点映射的 OrderHub 是同一类型
await hubContext.Clients.User(userId).SendAsync("OrderShipped", payload, ct);
```

推送是尽力而为的：没有在线连接、背板不可用时消息丢失。Worker 不需要本包的身份解析与 Hub 过滤器：那些只作用于客户端调用 Hub 方法。
实时通信与通知各自的 Worker 接线见[实时通信](./realtime.md#非-hub-进程发布)与[通知](./notifications.md#非-hub-进程发布)。

## 注意事项

- `AddSignalRAmbientContext` 自己补齐 Hub 调用所需的非 HTTP 环境上下文，宿主无需先注册。同时有 Controller/HTTP 路径时再调宿主 security 包的注册入口，把主体来源换成 `HttpContext.User`；两者调用顺序无关。
- 复评默认不节流；高频 Hub 应按实测配置 `RevalidationInterval`。
- 复评只评估策略的 `Requirements`，不涉及认证方案；身份来自握手时已认证的连接主体。会话或令牌被撤销不会在复评中被发现，只有按持久状态判定的 requirement（如账号停用）能让复评失败。
- 复评时 `resource` 为 `null`、没有 `HttpContext`，每次方法调用是新的 DI 作用域：handler 不能依赖请求中间件准备的数据。
- 本包只提供基座，不映射任何 Hub 端点，也不注册背板。
- 本包不配置 `HubOptions`：心跳、超时、详细错误是 SignalR 自身的选项，由宿主用 `AddSignalR(o => ...)` 直接配置。

## 相关

- [当前用户与身份信息](./security.md)
- [多租户](./multi-tenancy.md)
- [链路追踪](./tracing.md)
- [实时通信](./realtime.md)
- [通知](./notifications.md)
