# Leistd 组件总览

本页是 Leistd 框架按功能分组的组件索引。当前 `framework/components/` 共有 **26 个能力分组、69 个 NuGet 包**；DDD 四层基座的 4 个包另见 [DDD 四层基座](../ddd-struct/ddd-struct.md)。

## 组件清单

| 分组 | 一句话定位 | 包 | 文档 |
| --- | --- | --- | --- |
| 动态代理拦截器基类 | 基于 Castle DynamicProxy 的异步拦截器基类，统一同步/异步方法拦截入口并按 Order 排序织入 | `Leistd.DynamicProxy` | [`aop`](./aop.md) |
| SignalR 基座 | 为每次 Hub 调用建立环境上下文（主体/租户/链路标识）并复评宿主授权策略 | `Leistd.AspNetCore.SignalR` | [`aspnetcore-signalr`](./aspnetcore-signalr.md) |
| 后台作业 | 周期任务按对齐 UTC 的时段执行，登记时显式选择全集群一份（锁 + 水位）或每副本一份；进程内队列把请求外的工作挪到后台并带上入队时的上下文 | `Leistd.BackgroundJobs.Core`、`Leistd.BackgroundJobs.InProcess`、`Leistd.BackgroundJobs.EntityFrameworkCore` | [`background-jobs`](./background-jobs.md) |
| 审计 | 通过标记接口声明实体的创建/修改/删除审计能力；创建审计在实体进入变更跟踪时落定，修改和删除审计在保存时处理 | `Leistd.Auditing.Core`、`Leistd.Auditing.EntityFrameworkCore` | [`auditing`](./auditing.md) |
| 权限授权 | 基于具名权限的细粒度授权：`IPermissionChecker` 统一检查入口，声明式定义权限并接入 ASP.NET Core 策略管道；自带权限管理端点与首次授予 | `Leistd.Authorization.Core`、`Leistd.Authorization.AspNetCore`、`Leistd.Authorization.EntityFrameworkCore` | [`authorization`](./authorization.md) |
| 资源实例授权 | 对已加载的单个资源实例裁决：走官方授权管线，领域规则处理器与资源 ACL 合并，拒绝优先、默认拒绝；并提供把 ACL 合并进集合查询的入口 | `Leistd.Authorization.Resource.Core`、`Leistd.Authorization.Resource.AspNetCore`、`Leistd.Authorization.Resource.EntityFrameworkCore` | [`authorization-resource`](./authorization-resource.md) |
| 数据范围 | 把"能看到哪些候选数据"翻译成可由数据库执行的查询谓词，多个范围取并集；不内置组织模型 | `Leistd.Authorization.DataScope.Core` | [`authorization-data-scope`](./authorization-data-scope.md) |
| 核心原语 | 时钟抽象（IClock/UtcClockProvider）、环境上下文契约、释放动作与文本脱敏 | `Leistd.Core` | [`core`](./core.md) |
| 数据访问共享契约 | 连接解析、连接归属、分页、异步流过滤状态及 provider 中立的查询执行契约；EF 实现为可选包 | `Leistd.Data`、`Leistd.Data.EntityFrameworkCore` | [`data`](./data.md) |
| 服务注册回调与拦截器织入 | DI 包提供服务注册回调；DynamicProxy 扩展包在此基础上按约定织入 AOP 拦截器。 | `Leistd.DependencyInjection`、`Leistd.DependencyInjection.DynamicProxy` | [`dependency-injection`](./dependency-injection.md) |
| 邮件发送 | 统一的 IEmailSender 抽象与 SMTP 实现：发送失败抛异常，不需要投递的环境显式注册空发送器 | `Leistd.Email.Core`、`Leistd.Email.Smtp` | [`email`](./email.md) |
| 事件总线 | 进程内发布/订阅事件总线，发布方与 IEventHandler 处理器解耦，由 DI 同步消费 | `Leistd.EventBus.Core`、`Leistd.EventBus.Local` | [`event-bus`](./event-bus.md) |
| 业务异常与全局异常处理 | 语义化业务异常体系 + ASP.NET Core 全局处理器，统一转换为 RFC 9457 ProblemDetails 响应 | `Leistd.ExceptionHandling.Core`、`Leistd.ExceptionHandling.AspNetCore` | [`exception-handling`](./exception-handling.md) |
| 分布式锁与本地锁 | 统一的加锁抽象 ILock，可在内存（单机）与 Redis（分布式）实现间按 DI 注册切换。 | `Leistd.Lock.Core`、`Leistd.Lock.Memory`、`Leistd.Lock.Redis` | [`lock`](./lock.md) |
| 多语言本地化 | 基于嵌入 JSON 资源的 IStringLocalizer 实现：文案随包分发、按 culture 查表，未命中时返回键本身 | `Leistd.Localization.Core`、`Leistd.Localization.AspNetCore` | [`localization`](./localization.md) |
| 多租户 | 租户环境上下文（AsyncLocal 可切换）、请求级解析与校验中间件、`IMultiTenant` 数据隔离标记与写入落值、租户注册表存储；租户管理用例（创建开通与补偿、连接登记）单独成包并自带端点，资源服务经远端存储回源控制面 | `Leistd.MultiTenancy.Core`、`Leistd.MultiTenancy.Management`、`Leistd.MultiTenancy.AspNetCore`、`Leistd.MultiTenancy.EntityFrameworkCore`、`Leistd.MultiTenancy.ServiceClient` | [`multi-tenancy`](./multi-tenancy.md) |
| 通知 | 站内通知统一发布入口：先写入用户历史，再通过 SignalR 推送给该用户；自带通知中心端点、保留期清理，以及按用户设置过滤与邮件渠道两个桥接包 | `Leistd.Notifications.Core`、`Leistd.Notifications.EntityFrameworkCore`、`Leistd.Notifications.AspNetCore.SignalR`、`Leistd.Notifications.AspNetCore`、`Leistd.Notifications.Settings`、`Leistd.Notifications.Email` | [`notifications`](./notifications.md) |
| 对象映射 | 统一的 IObjectMapper 对象映射抽象，实现为 Mapster。 | `Leistd.ObjectMapping.Core`、`Leistd.ObjectMapping.Mapster` | [`object-mapping`](./object-mapping.md) |
| 操作记录 | 关键操作的审计留痕：什么人在什么时间做了什么、结果如何；业务显式调用，授权阶段的拒绝按注解补记 | `Leistd.OperationRecords.Core`、`Leistd.OperationRecords.EntityFrameworkCore`、`Leistd.OperationRecords.Logging`、`Leistd.OperationRecords.AspNetCore` | [`operation-records`](./operation-records.md) |
| 实时通信 | 通用业务事件实时推送通道：按 resourceKey 订阅与订阅授权扩展点，基于 SignalR 实现 | `Leistd.RealTime.Core`、`Leistd.RealTime.AspNetCore.SignalR` | [`realtime`](./realtime.md) |
| 统一 API 响应 | 统一 {code, message, data} 响应模型与 ASP.NET Core 自动包装过滤器 | `Leistd.Response.Core`、`Leistd.Response.AspNetCore` | [`response`](./response.md) |
| 设置 | 运行期可改的设置：业务声明定义与值域，框架按 用户 → 租户 → 代码默认值 回落解析并持久化；自带设置页端点，宿主级设置可经配置源覆盖部署配置 | `Leistd.Settings.Core`、`Leistd.Settings.EntityFrameworkCore`、`Leistd.Settings.AspNetCore`、`Leistd.Settings.Hosting` | [`settings`](./settings.md) |
| 服务间调用客户端 | 服务互调标准管道：Refit 与手写强类型客户端注册、链路标识透传、client credentials 与 Token Exchange 认证（令牌缓存、401 后重新获取）、响应读取与远端错误还原 | `Leistd.ServiceClient.Core`、`Leistd.ServiceClient.Refit`、`Leistd.ServiceClient.OAuth`、`Leistd.ServiceClient.AspNetCore` | [`service-client`](./service-client.md) |
| 身份与会话安全 | 主体访问、请求信息、Cookie 票据、浏览器来源防护、一次码和可选 OpenIddict 集成。 | `Leistd.Security.Core`、`Leistd.Security.AspNetCore`、`Leistd.Security.OneTimeCodes`、`Leistd.Security.OpenIddict.Validation`、`Leistd.Security.OpenIddict.Server` | [`security`](./security.md) |
| 关联标识 | 业务关联标识：默认等于 `Activity` 的 TraceId，可由调用方或入口显式指定；写入日志作用域，在 ASP.NET Core 入站、HttpClient 出站与后台任务之间传递。 | `Leistd.Tracing.Core`、`Leistd.Tracing.AspNetCore`、`Leistd.Tracing.HttpClient` | [`tracing`](./tracing.md) |
| 工作单元与事务 | 用 [UnitOfWork] 特性与 AOP 拦截器声明式管理数据库事务边界，并按提交阶段编排领域事件发布。 | `Leistd.UnitOfWork.Core`、`Leistd.UnitOfWork.EntityFrameworkCore` | [`unit-of-work`](./unit-of-work.md) |

## 定制组件行为

带端点的组件（设置、权限、多租户、操作记录、通知）按"契约与用例 / 存储 / `Map*` 端点"分包。需要偏离默认行为时，从上往下选第一个够用的层级：

| 层级 | 做法 | 适用 |
| --- | --- | --- |
| 配置 | 组件选项与配置节 | 策略名、保留天数、调度时间、作业开关 |
| 端点约定 | 在 `Map*` 返回的路由组或外层 `MapGroup` 上加约定；按组件公开的 `NamePrefix` 常量对单个端点追加元数据 | 路由前缀、限流、OpenAPI 标签、`WithResultWrapper()`、两步验证放行 |
| 钩子 | 实现组件声明的窄接口 | 主体目录、租户开通与启用前置、设置值校验、通知收件人解析 |
| 替换服务 | 组件用 `TryAdd` 注册，先于组件注册或 `Replace` 即可替换 | 换存储实现、装饰查询用例（只能收窄可见范围） |
| 自写端点 | 不调 `Map*`，在 Core 的用例服务上写自己的端点 | 路由形状或 DTO 与组件不同、聚合接口、换导出格式 |

组件状态变化（设置写入、授权替换、租户增删改）以本地事件发出，宿主订阅即可留痕或联动，不需要包装组件服务。

## 依赖关系

组件间依赖以各包 csproj 的 `ProjectReference`（打包后即 NuGet 包依赖）为唯一事实，本页不另画依赖边。分组按下列层次组织，下层分组不引用上层分组：

| 层次 | 分组 | 说明 |
| --- | --- | --- |
| 基础原语 | `aop`、`core`、`data`、`event-bus`、`exception-handling`、`localization`、`lock`、`object-mapping` | 不引用其他分组 |
| 运行时基座 | `dependency-injection`、`security`、`tracing`、`email`、`response`、`unit-of-work`、`service-client`、`aspnetcore-signalr`、`auditing`、`realtime`、`background-jobs` | 只引用基础原语和本层分组 |
| 带存储与端点的组件 | `multi-tenancy`、`authorization`、`authorization-resource`、`authorization-data-scope`、`settings`、`operation-records`、`notifications` | 可引用以上各层和本层分组 |

具体某个包引用了哪些分组，查看该包的 csproj 或 NuGet 依赖。组件不依赖 `ddd-struct`，方向是 `Leistd.Ddd.*` 引用组件包。
