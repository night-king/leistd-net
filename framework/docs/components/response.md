# 统一 API 响应

`Result` / `Result<T>` 定义统一响应，ASP.NET Core 过滤器自动包装控制器返回的普通成功对象。

## 何时使用

| 场景 | 用法 |
| --- | --- |
| 只需要统一响应的数据模型（如在应用层/领域层构造返回结构、跨项目共享契约） | 只引用 `Leistd.Response.Core` |
| ASP.NET Core Web API 需要自动包装控制器返回值 | 引用 `Leistd.Response.AspNetCore` 并注册过滤器 |
| Minimal API 端点（含组件经 `Map*` 提供的端点）也要包装 | 在路由组上调 `WithResultWrapper()` |
| 个别接口（如文件下载、第三方回调、健康检查）不希望被包装 | 在 action 或 controller 上标注 `[NoWrap]` |
| 想显式构造成功响应而非依赖自动包装 | 使用 `OkResult` Controller 扩展方法；失败一律 `throw` 业务异常，由异常处理管道写出 |

本组件是可选的数字信封协议，适用于 Java/旧系统对接等明确要求 `{ code, message, data }` 的项目。默认项目优先使用[异常处理组件](./exception-handling.md)的 RFC 9457 Problem Details。一旦启用 `AddResponseWrapper()`，它会同时替换异常和自动模型校验的写出形状，保证同一宿主只有一套失败协议。

## 安装

```bash
# 抽象与响应模型（可单独引用）
dotnet add package Leistd.Response.Core

# ASP.NET Core 集成（自动包装过滤器、Controller 扩展、NoWrap 特性）
dotnet add package Leistd.Response.AspNetCore
```

`Leistd.Response.AspNetCore` 已通过项目引用传递依赖 `Leistd.Response.Core`，Web 项目通常只需添加前者。

## 注册

在 `Program.cs` 的 MVC 链上挂载：

```csharp
builder.Services.AddControllers()
    .AddJsonOptions(options => { /* 宿主自己的序列化配置 */ })
    .AddResponseWrapper();
```

`AddResponseWrapper` 把 `ResultWrapperFilter`（一个 `IAsyncResultFilter`）加入 MVC 过滤器管线，并注册一个排在最前的 `IProblemDetailsWriter`：经 `IProblemDetailsService` 写出的失败（异常、自动模型校验、状态码页等）都写成信封。
它会幂等地启用 `ConfigureApiValidation()`，两者调用顺序无关。


Minimal API 端点不经过 MVC 过滤器，在路由组上挂端点过滤器：

```csharp
app.MapGroup("/api/v1")
    .WithResultWrapper()
    .MapGet("/orders/{id}", (long id, OrderService service) => service.GetAsync(id));
```

## 使用

注册过滤器后，控制器可以直接返回业务对象，框架自动包装：

```csharp
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/users")]
public class UserController(IUserService userService) : ControllerBase
{
    [HttpGet("{id}")]
    public async Task<IActionResult> GetAsync(long id)
        => Ok(await userService.GetAsync(id));

    [HttpGet("export")]
    [NoWrap]
    public IActionResult Export() => File(userService.ExportCsv(), "text/csv", "users.csv");
}
```

也可以用 Controller 扩展方法显式构造成功响应。失败不在控制器里手工构造：抛出业务异常，由异常处理管道写出，
启用包装时同样得到带 `traceId` 与 `errorCode` 的信封：

```csharp
using Microsoft.AspNetCore.Mvc;

public class OrderController(IOrderService service) : ControllerBase
{
    [HttpPost]
    public IActionResult Create(CreateOrderInput input)
    {
        var order = service.Create(input);
        return this.OkResult(order, "下单成功");
    }
}
```

## 接口参考

`Leistd.Response.Wrappers` 命名空间（响应模型，均为 `record`）：

| 成员 | 说明 |
| --- | --- |
| `Result` | 统一响应基类型，包含 `Code`、`Message` 及可选的 `TraceId`、`ErrorCode`、`Errors`；`Code = 0` 表示成功 |
| `Result.Ok(message?)` | 构造成功响应，`Code = 0` |
| `Result.Fail(code, message)` | 构造失败响应，指定业务码与消息 |
| `Result<T>` | 带数据负载的响应，继承 `Result`，新增 `Data`（失败时为 `null`） |
| `Result<T>.Ok(data, message?)` | 构造带数据的成功响应，`Code = 0` |
| `Result<T>.Fail(code, message?)` | 构造带数据类型但无数据的失败响应（`new` 隐藏基类同名方法） |
| `Result` 的失败形态 | 异常/校验信封：数字 `Code`、`Message`、`TraceId`、可选 `Errors` 与稳定字符串 `ErrorCode`；可选字段为空时不输出 |

`ErrorItem` 由 `Leistd.ExceptionHandling.Core` 定义（`Leistd.Response.Core` 已传递引用），是框架内唯一的字段错误形状：Problem Details 的 `errors`、本信封的 `errors`、服务客户端的 `RemoteServiceException.Errors` 用的都是它。

`Leistd.Response.AspNetCore` 命名空间（ASP.NET Core 集成）：

| 成员 | 说明 |
| --- | --- |
| `AddResponseWrapper(mvcBuilder)` | 把 `ResultWrapperFilter` 挂到宿主的 MVC 链（`IMvcBuilder` 扩展方法） |
| `WithResultWrapper(builder)` | 给端点或路由组挂 `ResultWrapperEndpointFilter`（`IEndpointConventionBuilder` 扩展方法） |
| `NoWrapAttribute`（`[NoWrap]`） | 标注在 action 或 controller 上跳过自动包装；`AttributeUsage = Method \| Class` |
| `ControllerExtensions.OkResult<T>(data, message?)` | 返回 HTTP 200 的 `Result<T>` 成功响应 |
| `ControllerExtensions.OkResult(message?)` | 返回 HTTP 200 的无数据 `Result` 成功响应 |

> Controller 扩展方法均为 `this ControllerBase` 扩展，调用时写作 `this.OkResult(...)`。

## 实现行为

### Leistd.Response.AspNetCore（自动包装过滤器）

- `ResultWrapperFilter` 仅包装满足以下全部条件的结果：结果为 `ObjectResult`、其 `Value` 不是 `Result`、HTTP 状态码为 `null` 或落在 200–299 区间。
- 命中包装时，原值被包成 `Result<object?>.Ok(value)`，状态码保留原值（无则取 200）；包装时输出一条 `Debug` 级日志。
- 标注了 `[NoWrap]`（通过 `EndpointMetadata` 检测）的接口直接放行，不做包装。
- 全局异常与 DataAnnotations 自动校验失败输出 `Result`。`code` 保持数字契约（默认为 HTTP 状态码），`errorCode` 保留可供客户端分支的稳定业务码。

### Leistd.Response.AspNetCore（端点包装过滤器）

- `ResultWrapperEndpointFilter` 只包装处理器直接返回的对象与 `TypedResults.Ok(value)`。
- 其余 `IResult` 原样放行（`Created`、`Accepted`、文件与流、重定向、`NoContent` 与非 2xx），以保留响应头与内容类型；
  需要信封时由端点自己放进结果，如 `TypedResults.Created(location, Result<T>.Ok(dto))`。
- 已是 `Result` 的值、带 `NoWrapAttribute` 元数据的端点同样原样放行。
- `WithResultWrapper()` 同时改写 200 响应的类型元数据，使 OpenAPI 与实际响应一致。按处理器的返回类型决定：
  - 裸值（含 `Task<T>`、`ValueTask<T>`，以及声明成 `object` 的）→ 该端点的每条 200 元数据都改写；
  - `Ok<T>` 与 `Results<Ok<A>, Ok<B>, …>` → 只改写落在这些 `Ok<T>` 上的 200 元数据，同为 200 的 `Json<T>` 分支不动；
  - `JsonHttpResult<T>`、文件、流、重定向、裸 `IResult`，以及拿不到处理器 `MethodInfo` 的自定义端点源 → 一律不改。
- 不要用 `object` 返回异构的 `IResult`：框架按裸值改写该端点的每条 200 元数据（含手工 `.Produces<T>()`），而运行期非 `Ok<T>` 的结果原样放行。
  写成具体类型、`Results<...>` 或 `IResult`；整个端点不该被包装时标 `[NoWrap]`。
- 标了 `[NoWrap]` 的端点既不包装响应也不改元数据。

## 注意事项

- MVC 过滤器只自动包装成功（2xx）的 `ObjectResult`；失败抛 `BusinessException`，由异常处理组件写成 `Result`，不在 Controller 手写 `try/catch`。
- 异常分类、错误码、HTTP 状态和本地化仍由异常处理组件的 `ExceptionDescriptor` 决定，信封只是传输格式。
- 异常管道写出的失败信封中，`code` 是 HTTP 状态码，稳定业务码在 `errorCode`，并带 `traceId`。直接在 Core 构造 `Result.Fail(code, message)` 时没有 HTTP 上下文，`code` 与 `traceId` 由调用方给出，仅用于不经异常管道的场景。
- 返回值本身已是 `Result`（如自行调用 `OkResult`）时不会被二次包装。
