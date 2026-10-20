# 关联标识

关联标识是业务层面的请求标识：默认等于当前 `Activity` 的 TraceId，调用方或入口也可以显式指定，并在上下文、日志、后台任务与下游 HTTP 调用间传递。

## 何时使用

| 场景 | 接入方式 | 包 |
| --- | --- | --- |
| ASP.NET Core Web 应用，需从请求头读取关联标识、写进日志并写回响应 | `UseCorrelationId()` 中间件 | `Leistd.Tracing.AspNetCore` |
| 通过 `HttpClient` 调用下游服务，需把当前关联标识透传过去 | `AddCorrelationIdForwarding()` | `Leistd.Tracing.HttpClient` |
| Hub 调用、后台作业、消息消费等无 HTTP 入口的场景 | `IAmbientContext.Begin(principal, correlationId)` | `Leistd.Core` + `Leistd.Tracing.Core` |
| 业务代码只需读取/切换当前关联标识 | 注入 `ICorrelationIdProvider` | `Leistd.Tracing.Core` |

## 安装

```bash
dotnet add package Leistd.Tracing.Core
dotnet add package Leistd.Tracing.AspNetCore
dotnet add package Leistd.Tracing.HttpClient
```

## 注册

### Web 应用

```csharp
builder.Services.AddCorrelationId();

var app = builder.Build();
app.UseCorrelationId();
```

`UseCorrelationId` 应尽量靠前，以便后续日志和中间件共享同一关联标识。

### 非 Web 宿主

没有 ASP.NET Core 入口（如 Worker Service）时只注册核心服务：

```csharp
services.AddCorrelationIdCore();
```

### HttpClient 出口转发

宿主自己配置的第三方客户端按需挂上；调用本系统内部服务走 ServiceClient，注册了关联标识时自动转发。

```csharp
builder.Services.AddHttpClient<ThirdPartyClient>()
    .AddCorrelationIdForwarding();
```

## 使用

### 读取和切换

```csharp
public class OrderService(ICorrelationIdProvider correlationId, OrderSubmitter submitter)
{
    public async Task RetryAsync(Order order, CancellationToken ct)
    {
        // 重试沿用下单时的关联标识：两次执行是两条链路，但在日志里按同一个标识串起来
        using (correlationId.Change(order.CorrelationId))
        {
            await submitter.SubmitAsync(order, ct);
        }
    }
}
```

### 非 HTTP 入口

消息消费、自建宿主的作业入口用环境上下文建立作用域，带上消息里的关联标识：

```csharp
using (ambientContext.Begin(principal, correlationId: message.CorrelationId))
{
    await handler.HandleAsync(message, ct);
}
```

不指定时，当前有 `Activity` 就沿用它的 TraceId，没有就新建。作用域内同时打开日志作用域 `leistd.correlationId`，与 HTTP 中间件一致。进程内后台队列（`Leistd.BackgroundJobs.InProcess`）入队时自动捕获关联标识与链路，执行时还原，工作项的日志（含失败日志）都带着入队时的关联标识。

## 接口参考

| 成员 | 说明 |
| --- | --- |
| `ICorrelationIdProvider.Get()` | 当前关联标识：显式 `Change()` 的值优先，其次当前 `Activity.TraceId`，都没有为 `null` |
| `ICorrelationIdProvider.Change(correlationId)` | 在返回的作用域内切换，可嵌套，释放时恢复上一层 |
| `CorrelationIdProvider` | 默认实现，`AsyncLocal` 保存显式值（Singleton）；宿主可先注册自己的实现替换 |
| `CorrelationIdConstants.LogKey` | 日志作用域键名 `leistd.correlationId` |
| `CorrelationIdConstants.MaxLength` / `IsWellFormed(value)` | 入站值的约束：不超过 64，只含 ASCII 字母、数字、`-`、`_` |

| 方法 | 所在包 | 说明 |
| --- | --- | --- |
| `AddCorrelationIdCore(configure?, configSectionPath?)` | Core | 注册 Provider 与环境上下文维度，绑定 `Leistd:CorrelationId` 并启动期校验 |
| `AddCorrelationId(configure?, configSectionPath?)` | AspNetCore | 调用 `AddCorrelationIdCore` |
| `UseCorrelationId()` | AspNetCore | 挂载入站中间件 |
| `AddCorrelationIdForwarding()` | HttpClient | 在命名/类型化客户端上挂载出站处理器；只影响挂载了它的客户端 |

## 配置项（`Leistd:CorrelationId`）

| 属性 | 默认值 | 说明 |
| --- | --- | --- |
| `Enabled` | `true` | 是否经 HTTP 传播（入站读取、响应回写、出站转发），全局生效。用于面向公网、不采信外部请求头的边缘服务：关闭后调用方不能往本服务的日志与操作记录里写入自选标识，进程内关联照常取 TraceId。只是不想向某个第三方转发时，那个客户端不挂 `AddCorrelationIdForwarding()` 即可（宿主自己配置的 HttpClient；ServiceClient 管道在注册了关联标识时自动转发，面向本系统的内部服务） |
| `HeaderName` | `X-Correlation-Id` | 读取、回写与转发的请求头名；空白时启动失败 |
| `SetResponseHeader` | `true` | 是否把关联标识写入响应头（响应里已有同名头时不覆盖） |

入站与出站每次都读取 `IOptionsMonitor<CorrelationIdOptions>.CurrentValue`，配置重载对后续请求生效。

## 与 W3C Trace Context 的关系

- 链路追踪用 W3C `traceparent` 与 `Activity`，由 .NET 自动传播；关联标识用 `X-Correlation-Id`，可以跨多条链路。默认情况下两者相等。
- 入站取值顺序：合法的入站 `X-Correlation-Id` → 当前 `Activity.TraceId` → 新建。非法值被丢弃（记 Debug 日志），请求不失败。
- 错误响应的 `traceId` 是官方链路标识（当前 `Activity.Id`，取第二段检索），不是关联标识；中间件不改写 `HttpContext.TraceIdentifier`。

## 注意事项

- `Get()` 在没有显式值也没有 Activity 时返回 `null`，读取后需判空。
- `Change()` 返回的 `IDisposable` 必须释放，否则上下文不会恢复，会污染同一异步流的后续逻辑。
- 显式指定的值超过 64 个字符时，操作记录落库会截断；请求头传入的值超长会被丢弃。
- 日志键 `leistd.correlationId` 是跨服务约定键；要在日志里看到它，日志库（如 Serilog）需启用作用域富化。TraceId 由日志库按 `Activity` 另行记录。

## 相关

- [后台任务](./background-jobs.md)
- [异常处理](./exception-handling.md)
