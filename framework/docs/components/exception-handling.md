# 业务异常与全局异常处理

`Leistd.ExceptionHandling.Core` 定义业务失败契约与不依赖 ASP.NET Core 的 HTTP 默认状态映射；`Leistd.ExceptionHandling.AspNetCore` 在 API 边界写出 RFC 9457 Problem Details。业务层不决定 HTTP 状态码，Controller 也不编写重复的 `try/catch`。

## 何时使用

| 场景 | 选择 |
| --- | --- |
| 参数空值、范围、状态或技术失败 | 优先 .NET 内置异常，如 `ArgumentException`、`InvalidOperationException`、`HttpRequestException` |
| Domain/Application 需表达可预期、可恢复的业务规则失败 | `BusinessException` |
| 请求 DTO 校验 | DataAnnotations；`[ApiController]` 自动返回 400 |
| 内部调用执行 DataAnnotations 校验 | `System.ComponentModel.DataAnnotations.ValidationException` |
| Web API 统一错误响应 | `Leistd.ExceptionHandling.AspNetCore` |

## 安装

```bash
dotnet add package Leistd.ExceptionHandling.Core
dotnet add package Leistd.ExceptionHandling.AspNetCore
```

## 注册

```csharp
// 绑定 Leistd:GlobalException，再应用这里的错误码映射
builder.Services.AddGlobalExceptionHandler(options =>
{
    options.MapCode("Order:NotFound", StatusCodes.Status404NotFound);
    options.MapCode("Order:VersionConflict", StatusCodes.Status409Conflict);
});
builder.Services.AddControllers().ConfigureApiValidation();

var app = builder.Build();
app.UseCorrelationId();
app.UseGlobalExceptionHandler();
```

`UseGlobalExceptionHandler` 应尽量靠近管道前端，并放在关联 ID 中间件之后。`ConfigureApiValidation` 让 MVC 自动模型校验经同一管道写出，`errors[].field` 跟随宿主的 JSON 命名策略，请求体、查询参数与嵌套对象（`shippingAddress.postalCode`）一致；请求体读不成 JSON 时只报对应字段，不回显解析器的异常消息（关闭了 `AllowInputFormatterExceptionMessages`）。未匹配的 `BusinessException` 默认为 400；资源不存在、并发冲突、显式的 422 语义等由宿主在 API 组合根按稳定错误码映射。

`IValidatableObject.Validate` 产出的错误与特性错误同一口径：`MemberNames` 写 C# 属性名（`nameof(Roles)`），字段名同样换成 JSON 名；对不上任何属性的成员名原样使用，不给成员名的错误落在模型自身的键上。宿主启用了 DataAnnotations 本地化（`AddDataAnnotationsLocalization`）时，`ErrorMessage` 与特性的 `ErrorMessage` 一样作资源键，经宿主的 `DataAnnotationLocalizerProvider`（以该 DTO 类型为参数）取本地化器翻译，所以写不含运行时值的固定英文句；未启用时原文返回。`Validate` 里不必自己取本地化器或换算字段名。

```csharp
using System.ComponentModel.DataAnnotations;

public sealed record TwoFactorLoginInput : IValidatableObject
{
    public string? Code { get; init; }
    public string? RecoveryCode { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Code) && string.IsNullOrWhiteSpace(RecoveryCode))
            yield return new ValidationResult("Enter the verification code or a recovery code.", [nameof(Code)]);
    }
}
```

宿主只映射自己的错误码。框架组件的非默认状态由组件在自己的 `AddXxx` 里登记（经 `services.Configure<GlobalExceptionOptions>`），
各组件的默认状态列在各自文档里。组件用 `MapDefaultCode` / `MapDefaultException` 登记，宿主用 `MapCode` 与对同一类型的 `MapException` 覆盖，
与调用顺序无关。

`GlobalExceptionOptions` 与 `ExceptionDescriptor` 在 `Leistd.ExceptionHandling.Core`，不依赖 ASP.NET Core。默认 400 不需要映射。
HTTP 状态属于 API 契约，只在代码里声明，不从配置文件读取。

## 使用

| 位置 | 异常规范 |
| --- | --- |
| Domain | 业务不变量失败抛 `BusinessException(code, safeMessage)`；实体方法不知道 HTTP |
| Application | 用例规则失败抛 `BusinessException`；请求可达的输入校验用 DataAnnotations / `ValidationException` |
| Infrastructure | 传输、配置、解析、数据损坏使用 BCL 或专用技术异常；不把上游错误伪装成本地业务失败 |
| API | 认证/授权交给 ASP.NET Core；错误码到 HTTP 状态的映射放在组合根 |
| 启动/后台任务 | 使用 BCL/技术异常使启动失败或记录任务失败，不造一个伪 HTTP 异常 |

```csharp
if (order.Status == OrderStatus.Shipped)
{
    throw new BusinessException(
            "Order:AlreadyShipped",
            $"Order '{order.Id}' has already shipped.")
        .WithData("OrderId", order.Id);
}
```

`Code` 在构造时必填且不可变，同时是机器契约和本地化资源键。`Message` 必须是安全、可在未启用本地化时直接返回的默认文案，并且可能随异常对象进入日志（5xx 的异常链、宿主自己记录的异常）：不拼入邮箱、手机号等不能进日志的值，这类值交给 `WithData`；技术细节放入 `InnerException`。
能帮用户修正操作的非敏感输入可以回显；密码、令牌、连接串以及匿名场景中会帮助枚举账号的标识不得回显。
`WithData` 写入 `LocalizationData`，供资源文案的具名占位符使用；填好的文案作为 `detail` 返回给调用方，原值也可能进入操作记录与导出，只放这些读者可见的值。

## 默认映射与安全边界

| 异常 | 默认响应 |
| --- | --- |
| `BusinessException` | 错误码映射命中时使用配置状态，否则 400；返回本地化文案或安全 `Message` |
| `System.ComponentModel.DataAnnotations.ValidationException` | 400，类型 `validation-error` + `errors`；不带业务码 |
| `BadHttpRequestException`（Minimal API 绑定失败、请求体过大等框架判定的请求错误） | 处理器放行，由异常中间件按异常自带的状态码（400、413 等）写出标准问题详情；原始消息只进日志 |
| 请求被客户端取消 | 官方 `ExceptionHandlerMiddleware` 在调用处理器之前直接返回 499，不经本组件 |
| 其它异常 | 500，只有本地化标题与 `traceId`，不回显异常消息；原异常记 Error 日志 |

`UseGlobalExceptionHandler` 通过 `ExceptionHandlerOptions.StatusCodeSelector` 让异常中间件沿用 `BadHttpRequestException` 自带的状态码。
处理器不把 `HttpRequestException`、`ArgumentException` 等推断为 4xx/503，未经宿主映射时返回 500。

业务错误码（`code`）与公开文案（`detail`）只出现在 `BusinessException` 上。输入校验、未预期异常、上游故障这类协议层失败的契约就是 HTTP 状态码本身（RFC 9457 §4），响应只有本地化标题、`traceId` 与可选的 `errors`，不合成与状态码一一对应的错误码。

400 是客户端请求错误的广义默认；422 只在宿主明确要表达“请求内容语法成立，但无法按其指令处理”且客户端确实需要区分时，才通过 `MapCode` 显式使用。

响应字段 `traceId` 是当前 `Activity.Id`（W3C 格式 `00-<TraceId>-<SpanId>-<flags>`，取第二段检索；没有 Activity 时为 `HttpContext.TraceIdentifier`），不被关联标识覆盖；
异常日志记下同一个值。业务层面的关联标识见[关联标识](./tracing.md)，它在响应头 `X-Correlation-Id` 与日志作用域里。
预期的 4xx 记 Warning，只有 `TraceId`、`StatusCode`、`Code`、异常类型与路径；不记 `detail`、默认文案与参数，也不附带异常对象。
`detail` 面向提交请求的人，可能回显其输入。`traceId` 把日志与响应、请求链路对上；端点有操作记录时，按日志作用域与响应头里的关联标识（见[关联标识](./tracing.md)）对上审计内容。5xx 记 Error 并保留异常链与堆栈。

## 诊断与遥测

处理器成功处理的异常由处理器按上述级别记**一次**日志。`UseGlobalExceptionHandler` 沿用 ASP.NET Core 对 `IExceptionHandler` 已处理异常的默认抑制（`SuppressDiagnosticsCallback` 只额外放过处理器交还中间件写出的 `BadHttpRequestException`）：

| 通道 | 处理器成功处理的异常 |
| --- | --- |
| `Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware` 的 Error 日志 | 不再记录 |
| `Microsoft.AspNetCore.Diagnostics.HandledException` 诊断事件（DiagnosticListener，部分 APM 采集器据此报错） | 不再发出 |
| 计数器 `aspnetcore.diagnostics.exceptions` | 照常记录，带异常类型 |
| 请求耗时 `http.server.request.duration` 的 `error.type` | 最终状态 ≥ 500 时由处理器补上异常类型全名（已有同名标签不覆盖）；4xx 不带 |

真正未处理、响应已开始或处理器写出失败的异常不受影响，保留官方的 Error 日志、诊断事件与 `error.type`。按 `HandledException` 事件统计错误的采集器改用处理器日志或上述指标。

## 无响应体的错误状态码

所有失败响应都经 ASP.NET Core 的 `IProblemDetailsService` 写出（异常处理器、自动模型校验、状态码页、`Results.Problem()` 与框架各中间件）；
本组件在 `ProblemDetailsOptions.CustomizeProblemDetails` 上统一补 `traceId`，并按 `Title:{状态码}` 本地化框架给的默认标题。

生产环境的 Minimal API 请求体解析失败（400）、内容类型不符（415）、未匹配路由（404）、认证质询（401）、限流（429）等只写状态码、不写响应体
（`RouteHandlerOptions.ThrowOnBadRequest` 默认只在开发环境开启）。为这类响应补上响应体用 ASP.NET Core 标准的状态码页，只作用于 API 路径：

```csharp
app.UseGlobalExceptionHandler();
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api"),
    api => api.UseStatusCodePages());
```

状态码页响应包含 `status`、本地化的 `title` 与 `traceId`，遵循上述协议错误契约。

## 响应与本地化

默认响应为 Problem Details：

```json
{
  "type": "urn:leistd:problem:business-error",
  "title": "Not Found",
  "status": 404,
  "detail": "订单不存在",
  "code": "Order:NotFound",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

容器里有非泛型 `IStringLocalizer` 时（本地化组件的 `AddJsonLocalization` 会注册），处理器按 `Code` 查资源并用 `LocalizationData` 替换 `{Name}` 占位符；
未命中或未启用本地化时回落到安全 `Message`。

`IncludeExceptionDetails` 仅用于调试时输出 `stackTrace`，生产环境保持 `false`。

## 接口参考

- `GlobalExceptionOptions.MapCode(code, status)`：精确映射业务错误码。
- `GlobalExceptionOptions.MapException<TException>(...)`：宿主将自定义异常映射为 `ExceptionDescriptor`。
- `LocalizationPlaceholders.Fill(text, data)`（Core 包）：按 `LocalizationData` 的约定填 `{Name}` 占位符（序数、区分大小写，没有对应参数的原样保留）。处理器用它生成 `detail`；在别处按错误码展示同一句话（如操作记录的失败原因）时调用它，两处才一致。
- `GlobalExceptionOptions.MapDefaultCode` / `MapDefaultException<TException>`：组件在自己的 `AddXxx` 里登记默认映射；宿主显式映射优先，业务项目不调用这两个。
- 需要完全自定义某类异常的处理时，按 ASP.NET Core 的方式再注册一个 `IExceptionHandler`（`AddExceptionHandler<T>()`，按注册顺序尝试，返回 `false` 交给下一个）。只需改状态码或描述时用 `MapException`：它的委托拿得到异常实例，可以按属性分支。
- 个别路径需要其他错误格式（如 Webhook 回调要求的固定响应体）时，在 `AddGlobalExceptionHandler` 之前注册该格式的 `IExceptionHandler`，由它按 `HttpContext.Request.Path` 判断并返回 `true`；其余请求返回 `false` 交给全局处理器。
- `IProblemDetailsWriter`（ASP.NET Core）：替换失败响应的序列化形状。默认输出 Problem Details；`Leistd.Response.AspNetCore` 的 `AddResponseWrapper()` 注册一个排在最前的写入器，把全部问题详情写成数字信封。

`ExceptionDescriptor` 携带 HTTP 状态、可选的业务错误码与公开消息、可选字段错误与日志级别；协议层失败只给状态码。类型映射给出业务码与消息时，消息同样按 `Code` 查宿主本地化资源，未命中时使用提供的安全默认消息。自定义扩展必须维持这些安全边界。

## 配置项（`Leistd:GlobalException`）

| 属性 | 默认值 | 说明 |
| --- | --- | --- |
| `IncludeExceptionDetails` | `false` | 是否输出堆栈，仅用于调试 |

映射的状态码必须为 400–599，`MapCode` / `MapDefaultCode` 传入越界值时立即抛出。默认 400 不必显式登记。

## 注意事项

- Core 包不依赖 ASP.NET Core。不提供 UserFriendlyException 或 BadRequestException / NotFoundException 等 HTTP 命名异常，业务失败只用错误码分类。

## 相关

- [本地化](./localization.md)
- [统一 API 响应](./response.md)
