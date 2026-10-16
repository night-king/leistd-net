# 通知

站内通知：业务代码经 `INotificationPublisher` 发布，框架定案这条记录的身份、写入收件人历史（`INotificationStore`），再逐个外发渠道送出（`INotificationChannel`）。

## 何时使用

| 场景 | 推荐 |
| --- | --- |
| 需要给用户展示"通知历史"（铃铛列表、未读数） | 引入 `Leistd.Notifications.EntityFrameworkCore`，注册 `INotificationStore` |
| 需要通知到达即时弹出/刷新，无需等待用户刷新页面 | 引入 `Leistd.Notifications.AspNetCore.SignalR`，它注册一个 `INotificationChannel` |
| 同时需要历史记录 + 实时推送（最常见） | 两个实现包都引入，业务只注入 `INotificationPublisher` |
| 仅编写业务代码（发布通知），不关心底层持久化/传输 | 只引用 `Leistd.Notifications.Core` 中的接口 |
| 前端的通知中心（列表、未读数、标记已读、清空） | 引入 `Leistd.Notifications.AspNetCore`，路由组上调 `MapNotifications(configure)` |
| 旧通知要定期清理 | `AddNotificationRetention<TDbContext>()`，默认开启：已读保留 90 天、未读保留 365 天 |
| 用户可以按类型、渠道关闭通知 | 引入 `Leistd.Notifications.Settings`，`AddNotificationPreferences(...)` |
| 通知同时发邮件 | 引入 `Leistd.Notifications.Email`，`AddEmailNotifications()` 并实现收件人地址解析 |

## 安装

```bash
# 抽象 + 默认发布器（业务代码引用；实现包已传递引用，通常无需单独添加）
dotnet add package Leistd.Notifications.Core

# 持久化（存历史通知、未读数）
dotnet add package Leistd.Notifications.EntityFrameworkCore

# 实时推送（基于 SignalR）
dotnet add package Leistd.Notifications.AspNetCore.SignalR

# 通知中心 HTTP 端点
dotnet add package Leistd.Notifications.AspNetCore

# 桥接包：按用户设置决定投递、邮件渠道
dotnet add package Leistd.Notifications.Settings
dotnet add package Leistd.Notifications.Email
```

## 注册

在 `Program.cs` / DI 配置中按需注册：

```csharp
// 前置：发布器要给每条通知盖创建时刻，IClock 由宿主注册
// （Leistd.Core 不提供 DI 扩展；DDD 基础设施包已代为注册）
builder.Services.AddSingleton<IClock, UtcClockProvider>();

// EF 包同时接上 Core 的发布器：只要历史、不要实时推送时装它一个就成立
services.AddNotificationsEfCore<MyProjectDbContext>();

// 实时推送是可选关注点，另外装
builder.Services.AddNotificationsSignalR();
```

保留期清理的逐库遍历（`ITenantDatabaseRunner`）由 `AddMultiTenancyCore()` 提供，不分库时只有宿主库；组件不代为注册。

在 `OnModelCreating` 中应用通知实体的 EF Core 配置：

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ConfigureNotifications();
}
```

映射通知 Hub 端点（需登录）：

```csharp
app.MapNotificationHub();
```

`AddNotificationsSignalR()` 会同时注册 Core 发布器与 SignalR 传输，但不注册持久化，也不映射业务实时 Hub。`MapNotificationHub()` 默认映射到 `/hubs/notifications` 并要求登录：握手按 `HubIdentityOptions.PolicyName`（未设置时为默认策略）授权，与调用期复评同一策略。

同时使用[实时通信](./realtime.md)时，可以让通知与业务事件共用一个 Hub、一条客户端连接（ASP.NET Core 一个 Hub 对应一条连接）：把上面的 `AddNotificationsSignalR()` 换成以实时通信的 Hub 为类型参数的 `AddNotificationsSignalR<RealTimeHub>()`，并只映射实时通信的 Hub：

```csharp
builder.Services.AddRealTimeSignalR();

// 共用连接时只映射此 Hub。
app.MapRealTimeHub();
```

此时通知的接收授权就是该 Hub 的授权要求。通知只经一个 Hub 推送：同一个 Hub 重复注册是幂等的，已选定一个 Hub 后再指定另一个（包括先调用无泛型版本）在注册时抛 `InvalidOperationException`。

`AddNotifications()` 已登记本组件错误码的非默认 HTTP 状态（具体映射见[接口参考](#接口参考)），宿主不需要另行组合。

`INotificationChannel` 可注册多个；`INotificationStore` 必须且只能注册一个。只要瞬态推送而不要历史时使用[实时通信](./realtime.md)。

映射通知中心端点（只作用于当前用户）：

```csharp
// 策略名必填：组件不内置默认策略，也不在路由组上套宿主默认策略
app.MapGroup("/api/v1/notifications").MapNotifications(options => options.AccessPolicy = "App.CurrentUser");
```

启用保留期清理（需要后台作业调度器与分布式锁，见[后台作业](./background-jobs.md)）：

```csharp
builder.Services.AddMultiTenancyCore();            // 提供逐库遍历；不分库时清单只有宿主库
builder.Services.AddNotificationRetention<MyProjectDbContext>();
```

按用户偏好过滤投递、增加邮件渠道：

```csharp
using Microsoft.Extensions.DependencyInjection.Extensions;

builder.Services.AddNotificationPreferences(options =>
    options.MandatoryDeliveries.Add(new NotificationDelivery("Security", INotificationChannel.InAppName)));
builder.Services.AddEmailNotifications();
builder.Services.TryAddTransient<INotificationRecipientResolver, UserEmailRecipientResolver>();
```

邮件里附的链接：绝对 http(s) 地址原样附上；以 `/` 开头的站内链接在配置了 `Leistd:Notifications:Email:PublicBaseUrl` 时拼成绝对地址附上，未配置时不附。
站点地址直接拼在链接前：带路径前缀的站点写 `https://example.com/portal`，前端用哈希路由时写 `https://example.com/#`；不从请求推导。

偏好是宿主定义的用户级布尔设置，名为 `{前缀}.{通知类型}.{渠道名}`（前缀默认 `Notifications`，邮件渠道名为 `EmailNotificationChannel.ChannelName`）；没有定义的组合一律投递。

前置：宿主须已注册 `AddUnitOfWork()` 与 `AddUnitOfWorkEfCore()`，EF 存储经 `IDbContextProvider<TDbContext>` 取得本工作单元已解析连接的上下文。

## 使用

**发布通知**——注入 `INotificationPublisher`，发布给指定用户：

```csharp
public class OrderNotifier(INotificationPublisher notificationPublisher)
{
    public async Task ApproveOrderAsync(string userId, string orderNo)
    {
        await notificationPublisher.PublishToUserAsync(userId, new NotificationInputDto
        {
            Title = "订单已通过审批",
            Content = $"订单 {orderNo} 已通过审批",
            Type = "Approval", // 类别由业务项目自己定义
            Link = $"/orders/{orderNo}",
            RelatedEntityId = orderNo,
            RelatedEntityType = "Order",
        });
    }
}
```

查询通知历史只需注入 `INotificationStore`：

```csharp
public class MessageCenter(INotificationStore notificationStore)
{
    public async Task<IReadOnlyList<NotificationOutputDto>> GetLatestAsync(
        string userId,
        CancellationToken ct = default)
        => (await notificationStore.GetByUserAsync(userId, new PageRequest { Limit = 50 }, ct: ct)).Items;
}
```

## 接口参考

`Leistd.Notifications` 命名空间（`Leistd.Notifications.Core` 包）：

| 成员 | 说明 |
| --- | --- |
| `INotificationPublisher` | 通知发布器，业务层唯一入口，不感知底层传输 |
| `INotificationPublisher.PublishToUserAsync(userId, notification, ct)` | 推送给指定用户；`userId` 为 `string`，`notification` 为 `NotificationInputDto` |
| `INotificationChannel` | 通知外发渠道，只负责把已定案的通知送出去，由具体介质实现（如 SignalR）；业务代码用 `INotificationPublisher` |
| `INotificationChannel.Name` | 渠道名，投递过滤按它区分；站内实时推送用 `INotificationChannel.InAppName` |
| `INotificationChannel.DeliverAsync(userId, notification, ct)` | 投递给指定用户；`notification` 已由发布器补齐 `Id`/`CreationTime` |
| `INotificationDeliveryFilter.ShouldDeliverAsync(userId, notification, channel, ct)` | 这条通知是否经该渠道投给该用户；默认一律投递，宿主按用户偏好替换 |
| `INotificationChannel.InAppName` | 站内渠道名（`"InApp"`）：通知历史与实时推送共用这一个开关 |
| `INotificationStore` | 通知持久化接口；必需且只能有一个实现 |
| `INotificationStore.SaveAsync(notification, userId, ct)` | 保存通知 |
| `INotificationStore.GetByUserAsync(userId, page, unreadOnly, ct)` | 按创建时间倒序分页获取用户通知，`unreadOnly` 只取未读；返回 `PagedResult<NotificationOutputDto>` |
| `INotificationStore.DeleteAsync(notificationId, userId, ct)` / `DeleteAllAsync(userId, ct)` | 删除用户自己的一条 / 全部通知；删除账号时调用后者清理孤儿行 |
| `MapNotifications(configure)` | AspNetCore 包：`AccessPolicy` 必填；`GET /`（`maxCount` 收敛到 1–`NotificationEndpoints.MaximumListCount`，可加 `unreadOnly`）、`GET /unread-count`、`PUT /{id}/read`、`PUT /read-all`、`DELETE /`、`DELETE /{id}` |
| 默认 HTTP 状态 | 组件默认状态：`NotificationErrorCodes.IdentityCannotOperate` → 403。由 `AddNotifications()` 自动登记；宿主 `MapCode` 可覆盖 |
| `AddNotificationRetention<TDbContext>(configure?, configSectionPath?)` | EF 包：绑定 `configSectionPath`（默认 `Leistd:Notifications:Retention`）并启动期校验，校验消息按实际路径报键，重复调用换用另一配置节时抛出；登记集群周期任务 `notifications.retention` |
| `AddNotificationPreferences(configure?)` | Settings 桥接包：以 `NotificationPreferenceOptions`（前缀、必达组合）替换默认投递过滤器 |
| `AddNotificationsSignalR()` / `AddNotificationsSignalR<THub>()` | SignalR 包：通知经 `NotificationHub`（配 `MapNotificationHub`）或宿主指定的 Hub 推送；客户端方法名 `NotificationClientMethods.Received`（`Notifications.Received`） |
| `AddEmailNotifications(configure?, configSectionPath?)` / `INotificationRecipientResolver` | Email 桥接包：绑定 `Leistd:Notifications:Email`（`EmailNotificationOptions.PublicBaseUrl` 可选，配置时启动期校验为绝对 http(s) 地址），登记 `EmailNotificationChannel`，重复调用换用另一配置节时抛出；收件人地址由宿主解析，只发已验证地址，经后台队列异步发送 |
| `INotificationStore.MarkAsReadAsync(notificationId, userId, ct)` | 标记单条通知为已读 |
| `INotificationStore.MarkAllAsReadAsync(userId, ct)` | 标记用户所有通知为已读 |
| `INotificationStore.GetUnreadCountAsync(userId, ct)` | 获取用户未读通知数量 |
| `NotificationInputDto` | 发布输入：`Title`（必填）、`Content?`、`Type`（业务自定义字符串，默认 `"System"`）、`Link?`、`Icon?`、`RelatedEntityId?`、`RelatedEntityType?`、`Metadata?`；`Id`、`CreationTime`、`IsRead` 由发布器按收件人定案 |
| `NotificationOutputDto` | 读取与实时传输输出：在 `NotificationInputDto` 的字段上加 `Id`、`IsRead`、`CreationTime`；`Id` 是该用户那条记录的标识，标记已读时使用 |

## 实现行为

### Leistd.Notifications.Core（`NotificationPublisher` 默认发布器）

- `PublishToUserAsync` 先写入 Store，再依次调用所有渠道（`INotificationChannel`）；Store 写入失败终止发布。
- 投不投由 `INotificationDeliveryFilter` 决定：先问站内（`INotificationChannel.InAppName`），不投则既不写 Store、也不调用站内渠道；其余渠道逐个按 `Name` 问。
  默认过滤器一律投递；要按用户偏好过滤时，在 `AddNotifications` 之前注册自己的实现（或之后用 `Replace`）。过滤器抛出的异常会让本次发布失败。
- 创建时刻只来自 `IClock.Now`，发布输入不能指定。
- 通知按用户寻址并保留历史；realtime 按客户端订阅的资源寻址。两个组件默认使用各自的 Hub、可独立安装，也可经 `AddNotificationsSignalR<THub>()` 共用一个 Hub。
- 业务代码只注入 `INotificationPublisher`；直接调用 `INotificationChannel` 会绕过历史写入。
- 多个 `INotificationChannel` 按注入顺序串行 `await`，非并行、非后台任务。

### Leistd.Notifications.AspNetCore.SignalR（实时推送）

- `NotificationHub` 没有可供客户端调用的方法，也不做分组；它只是接收端点。
- 渠道用 `Clients.User(userId)` 寻址，经注册时选定的 Hub 推送；客户端方法名为 `NotificationClientMethods.Received`（`Notifications.Received`）。
  业务事件不要使用 `Notifications.` 前缀。
- `DeliverAsync` 不吞异常；`NotificationPublisher` 逐个渠道捕获、记 `LogError`、继续下一个，一个渠道送不到不会让 `PublishToUserAsync` 失败。
  调用方取消（`OperationCanceledException` 且 `ct` 已取消）如实向上传播。
- `AddNotificationsSignalR` 内部调用 `AddNotifications()` 与基座的 `AddSignalRAmbientContext()`，不碰 `HubOptions`。
- `MapNotificationHub` 只映射通知自身的 Hub；同时需要业务实时事件时另行调用 `AddRealTimeSignalR` 与 `MapRealTimeHub`，或用 `AddNotificationsSignalR<RealTimeHub>()` 共用一个 Hub。

### Leistd.Notifications.EntityFrameworkCore（持久化）

- `EfCoreNotificationStore<TDbContext>` 经 `dbContext.Set<NotificationRecord>()` 操作，宿主 DbContext 需在 `OnModelCreating` 调用 `ConfigureNotifications()`。
- `CreationTime` 由发布器定好、经 `FromDto` 带入，不依赖审计拦截器；`CreatorId` 仍由审计拦截器填充。
- `GetByUserAsync` 按 `CreationTime` 倒序、同刻按 `Id` 倒序排序；`PageRequest.Sorting` 不生效。
- 保留期清理按物理库逐个执行，每批一个事务；已读与未读都按创建时间计，未读的保留天数不得短于已读。
- 邮件渠道按纯文本发送正文，链接另起一段附上：绝对 http(s) 地址原样附上，以 `/` 开头的站内链接在配置了 `PublicBaseUrl` 时拼成绝对地址，其余不附；需要定制邮件版式时实现自己的 `INotificationChannel`。队列满时丢弃这一封并记警告，站内通知不受影响。
- `MarkAsReadAsync` 幂等：`notificationId` 不是 `Guid`、查不到记录或已读时直接返回；仅在从未读变为已读时更新 `IsRead` 与 `ReadAt` 并保存。
- `MarkAllAsReadAsync` 只批量标记未读记录；没有未读时不保存。
- 索引：`(UserId, CreationTime)`（通知列表）、`(UserId, IsRead)`（未读数）、单列 `CreationTime`（保留期清理整库按时间扫）。表名沿用 EF Core 默认约定。

## 非 Hub 进程发布

独立的 Worker 经 `INotificationPublisher` 发通知时，历史照常写入，站内推送经背板送到连在 API 节点上的收件人。Worker 的组合与 API 相同，只是不映射 Hub：

- 发布器的前提照旧：`IClock` 与唯一的 `INotificationStore`（用 EF 存储时连同它的数据库、工作单元与租户组合，见[注册](#注册)）；投递偏好、邮件等渠道按 API 的组合注册。
- `AddNotificationsSignalR<THub>()` 选与 API **同一个** Hub：API 让通知与实时通信共用实时通信的 Hub 时，Worker 也要以那个 Hub 为类型参数。
- 注册同一背板与同一通道前缀，不调用 `MapNotificationHub()`。

```csharp
using StackExchange.Redis;

// Worker：与 API 选同一个 Hub（这里 API 用的是通知自己的 Hub）
builder.Services.AddNotificationsSignalR();
builder.Services.AddSignalR().AddStackExchangeRedis(redisConnectionString, options =>
    options.Configuration.ChannelPrefix = RedisChannel.Literal("my-app:production:"));
```

背板、前缀、会话粘滞与失败语义见 [SignalR 基座](./aspnetcore-signalr.md#多实例部署)；推送失败按渠道隔离，不影响历史写入。

## 注意事项

- 本组件只面向人：每条通知都有归属用户、写入历史、计入未读数。推给订阅了某资源的连接（无历史）用[实时通信](./realtime.md)的 `IBusinessEventPublisher`。
- 本家族无独立 Options；心跳、超时用 `AddSignalR(o => ...)` 配，`UserIdentifier` 的 claim 解析顺序见 [SignalR 基座](./aspnetcore-signalr.md)。
- 通知 `Type` 与渠道名（`INotificationChannel.Name`，如 `"InApp"`、`"Email"`）是开放标识符，按定义原样传输与存储，不随 JSON 命名策略改写：
  它们落在 `NotificationRecord.Type` 并拼进偏好设置名（`{SettingNamePrefix}.{Type}.{Channel}`），改写法等于换身份，须迁移历史与偏好。
- 全员公告要进历史时由业务决定受众，逐个调用 `PublishToUserAsync`；框架不提供发给一个组的入口。
- `PublishToUserAsync` 为每次发布生成 `Id`（`Guid.CreateVersion7().ToString("N")`）与 `CreationTime`（`IClock.Now`），并把同一个对象先交给 Store、再交给所有渠道，
  库里与推送里的 ID 一致。`NotificationRecord.FromDto` 遇到不是 Guid 的 `Id` 抛 `ArgumentException`。
- 传给 `PublishToUserAsync` 的 `userId` 必须等于该客户端的 SignalR `UserIdentifier`（按 `ClaimTypeOptions.UserIds` 解析），否则推送静默落空。
- `PublishToUserAsync` 成功返回不代表实时推送送达（客户端可能未连接）；需要送达保证时依赖持久化历史与客户端拉取未读数。
- `NotificationHub` 要求登录；它没有客户端可调用的方法，授权只在握手时执行：账号之后被禁用或会话被撤销，既有连接不会被关闭，照常收到推送，直到断线重连（握手按 Hub 策略重新授权）或页面关闭。
  经 `AddNotificationsSignalR<RealTimeHub>()` 共用实时 Hub 时，连接还会在下一次 `Subscribe`/`Unsubscribe` 复评失败时中止，前提见[实时通信](./realtime.md#注意事项)。
- 发布方负责建立租户上下文：`NotificationRecord` 实现 `IMultiTenant`，`TenantId` 按实体进入跟踪时的当前租户落值。后台作业、消息消费者须先
  `ICurrentTenant.Change(tenantId)`（或 `IAmbientContext.Begin`）再发布，否则通知落成宿主行，表现为“推送收到了、未读数却是 0”。
- 多副本部署须配置 SignalR 背板，否则推送只到达连在本节点的客户端，见 [SignalR 基座](./aspnetcore-signalr.md#多实例部署)。
- 令牌到期不会自动断开连接（未配置 `CloseOnAuthenticationExpiration`）；确需到期即断时显式开启，并用真实 SignalR Client 验证。它只按认证到期断开，不感知账号禁用或撤销。
- 用 Bearer 认证时，浏览器客户端的令牌只能放进查询串，宿主须在认证之前接入 SignalR 基座的 `UseHubAccessToken()`，见 [SignalR 基座](./aspnetcore-signalr.md#注册)。

## 相关

- [当前用户与身份信息](./security.md)
