# 服务间调用客户端

服务调用统一处理链路标识、OAuth 机器认证、用户令牌交换与远程错误还原。Refit 是默认的业务客户端编写方式；身份由签发方令牌证明。

## 何时使用

| 场景 | 入口 | 包 |
| --- | --- | --- |
| Refit 业务客户端 | `AddRefitServiceClient` | `Leistd.ServiceClient.Refit` |
| 手写客户端与错误读取 | `AddServiceClient`、`ReadContentAsync`、`EnsureRemoteSuccessAsync` | `Leistd.ServiceClient.Core` |
| 工作负载身份与机器令牌 | `AddServiceAuthentication`、`AddClientCredentials` | `Leistd.ServiceClient.OAuth` |
| 单跳用户委托 | `AddTokenExchange` | `Leistd.ServiceClient.OAuth` |
| 当前请求的用户访问令牌 | `AddUserAccessTokenAccessor` 或 `AddAuthenticatedUserAccessTokenAccessor` | `Leistd.ServiceClient.AspNetCore` |

Core 的 `IUserAccessTokenAccessor` 只定义证明来源，不依赖 ASP.NET Core 或 OpenIddict。OAuth 复用官方 OpenIddict.Client 7.7 的发现文档与客户端认证，不推导令牌端点；机器回源与用户交换使用同一工作负载注册。

## 安装

```bash
dotnet add package Leistd.ServiceClient.Refit
dotnet add package Leistd.ServiceClient.OAuth
dotnet add package Leistd.ServiceClient.AspNetCore
```

声明 Refit 接口的项目直接引用 Refit，以激活源生成器；接口为 public。

## 注册

工作负载身份由宿主显式注册一次。机器端点和用户端点使用不同命名客户端：

```csharp
builder.Services.AddServiceAuthentication();
builder.Services.AddRefitServiceClient<IIdentityApi, IdentityOptions>("Identity")
    .AddClientCredentials();
builder.Services.AddRefitServiceClient<IBillingApi, BillingOptions>("Billing")
    .AddTokenExchange();
// 资源宿主传入实际的 Bearer 验证方案。Identity/Standalone 的 Cookie 不提供此证明。
builder.Services.AddUserAccessTokenAccessor("OpenIddict.Validation.AspNetCore");
```

客户端与认证入口统一先绑定配置，再应用可选委托 `configure`，并可用 `configSectionPath` 指定其他路径：`AddServiceClient` / `AddRefitServiceClient` 默认绑定 Leistd:ServiceClients:{serviceName}；`AddServiceAuthentication` 默认绑定 Leistd:ServiceAuth；`AddClientCredentials` 的默认路径由 builder.Name 派生为 Leistd:ServiceClients:{Name}；`AddTokenExchange` 派生为 Leistd:ServiceClients:{Name}:TokenExchange。认证选项在启动时验证，消息按实际路径报键。

重复调用的契约：

- 客户端按服务名区分，不同服务名可多次登记；同一服务名的相同登记（客户端接口、实现、选项类型与配置节都相同）重复调用不重复登记客户端与处理器，只追加 `configure`，返回同一命名客户端的构建器。同一服务名换用其他登记、或一个选项类型用于两个服务名时抛 `InvalidOperationException`——每个客户端一个具体选项类型。
- `AddServiceAuthentication` 只有一个工作负载身份：相同配置节重复调用幂等，换用另一配置节时抛出。
- 每个命名客户端只能安装一个认证处理器：同一方式、同一配置节重复调用幂等；换用另一方式或配置节时抛出。

访问令牌读取入口二选一，两者均注册 Singleton，不覆盖宿主实现；同时调用时首次登记的入口与方案生效：

| 入口 | 前提 |
| --- | --- |
| `AddUserAccessTokenAccessor(scheme)` | 请求携带 Bearer 头，指定 Bearer 方案认证成功 |
| `AddAuthenticatedUserAccessTokenAccessor(scheme)` | 宿主显式选择可信服务端会话或策略方案；指定方案认证成功 |

两者要求指定方案认证成功且主体至少有一个已认证身份，只读取票据保存的 `access_token`，不读取原始请求头。OpenIddict Validation 自动保存令牌；JwtBearer 需启用 SaveToken，OIDC 会话需由可信处理器验证并保存访问令牌。策略方案如何转发由宿主决定，组件不回退其他方案。

适配器在发送请求时读取 HttpContext，池化 handler 不捕获请求作用域。无请求、认证失败、未认证主体或无令牌时返回 null；取消照常传播。非 Web 宿主可实现 `IUserAccessTokenAccessor`，返回值必须是已验证用户访问令牌。

## 使用

### 请求与响应

Refit 客户端默认使用 System.Text.Json Web 约定。常用形态如下：

| 数据 | 声明 |
| --- | --- |
| JSON | `[Body] OrderDto dto` |
| URL 编码表单 | `[Body(BodySerializationMethod.UrlEncoded)]` |
| 文件上传 | `[Multipart]` + `StreamPart` / `ByteArrayPart` / `FileInfoPart` |
| Query | 方法参数，用 `[Query]` / `[AliasAs]` 调整命名 |
| 原始响应或文件下载 | `Task<HttpResponseMessage>` |

常规路径是裸载荷：本框架的服务成功时直出对象、失败时返回 RFC 9457 Problem Details。手写客户端使用：

```csharp
var response = await httpClient.GetAsync($"api/v1/orders/{id}");
var order = await response.ReadContentAsync<OrderDto>();
```

`ReadResultAsync<T>()` 仅用于被调方产出 `{code, message, data}` 信封的互操作路径；Refit 接口对应写成 `Task<Result<T>>`。两条路径的错误处理相同。

返回 `HttpResponseMessage` 的 Refit 方法不经错误工厂；读取流前必须调用 `EnsureRemoteSuccessAsync()`。

默认 Refit 配置通过 `TransportExceptionFactory` 原样传递发送阶段的异常，保留标准管道的故障分类与 .NET 原生取消语义。全局异常处理中，远端故障映射为 502、不可达映射为 503、弹性管道超时映射为 504。`Task<ApiResponse<T>>` 的发送阶段遇到组件异常时也会直接抛出，不写入 Refit 的 `Error`；自定义配置请从 `ServiceClientRefitSettings.Create()` 开始，以保留此契约。

远程错误映射为：

| 响应 | 结果 |
| --- | --- |
| 2xx 且 `code = 0` | 返回 `data` |
| 2xx 且 `code != 0` | 抛 `RemoteServiceException` |
| 非 2xx ProblemDetails | 还原 `code`、`detail`、`traceId` 和 `errors`。`errors` 认两种形状：Leistd 的数组 `[{field, detail, code}]`，与官方 `HttpValidationProblemDetails` 的字典 `{字段: [消息…]}`（`AddValidation()`、MVC 默认校验，键为属性名）；字典的每条消息各成一项 `ErrorItem`，`Code` 为空 |
| 非 2xx 数字信封 | 从 `errorCode` 还原稳定业务码；数字 `code` 是 HTTP 状态，不作为业务码 |
| 非 2xx 非 JSON | 保留最多 4096 字符的 `ResponseBody` |
| 网络、超时或反序列化失败 | 抛 `ServiceClientException`；调用方主动取消原样上抛 |

不要将远程状态直接当作本地业务结果；需要转换时捕获 `RemoteServiceException`，按 `RemoteStatusCode`、`ErrorCode` 和 `RemoteTraceId` 显式决策。
API 边界的默认响应：未处理的远端拒绝及无效或提前中断的响应返回 502（不透传上游状态或消息），本地配置及未分类故障返回 500，连接不可达返回 503，等待超时返回 504。
宿主知道某个上游的稳定契约时，可用 `MapException<RemoteServiceException>` 显式分类。

超时由宿主在返回的 `IHttpClientBuilder` 上叠加的弹性管道负责（如 `AddStandardResilienceHandler()`，只加一个）；其超时被包装为 `FailureKind = Timeout` 的 `ServiceClientException`，API 边界返回 504。
组件不设置 `HttpClient.Timeout`（.NET 默认 100 秒作外层兜底），它到期时抛出的 `TaskCanceledException` 不被包装。调整兜底时长用 `ConfigureHttpClient`，并保持它大于弹性管道的总超时。

### 身份与调用管道

```text
业务代码 → 传输故障分类 → 关联标识 → 机器认证或用户交换 → 网络
```

关联标识经追踪组件透传，未注册时直通。Core 不转发用户或租户请求头；下游从已验证 JWT 的主体读取用户与租户。

机器模式只代表 client credentials 的工作负载，不恢复自然人身份。用户模式通过 `IUserAccessTokenAccessor` 获取当前请求的访问令牌作为 subject；
ASP.NET Core 宿主按上面的注册契约选择证明来源；仅有 Cookie 或后台用户身份不能提供访问令牌证明。
没有令牌或请求预设 Authorization 时拒绝调用，不回退为机器身份。授权关系与目标 audience/scope 由签发方策略决定，下游仍按本地权限判定。

用户交换目标通过命名客户端的 Audience、Scope 指定。输出主体、声明与令牌有效期由身份服务决定；组件提交用户访问令牌作为 subject，不从环境用户或租户构造证明。

### 缓存与故障

组件默认关闭官方客户端令牌存储。令牌按官方客户端返回的到期时间缓存在进程内（HybridCache，DisableDistributedCache）：机器令牌提前 60 秒失效，用户交换令牌提前 10 秒失效；
同一实例内同键的并发抓取合并为一次。缓存键含签发方、工作负载、命名客户端、目标范围及完整 subject 令牌的 SHA-256 摘要。

401 清除对应缓存，下次调用重新获取；缓存后端删除故障记 Warning、不覆盖原响应；请求取消照常传播。组件不自动重放业务请求，调用方按幂等性决定重试。
令牌请求的协议拒绝归类为 `RemoteFailure`，API 默认返回 502，诊断保留官方错误码。

官方客户端 7.x 会在响应解析失败、非成功状态码且没有 OAuth error、成功状态码却取不出响应时把远端响应原文（含访问令牌）写进 Error 日志。
组件在发现文档、JWKS 和令牌响应上排在官方处理器之前接手这几步：取消照常传播，其余失败按官方相同的错误码拒绝，日志只记录状态码、Content-Type 与长度。
官方客户端 7.x 发送请求时还会把调用方主动取消记成网络故障（Error 日志）并拒绝为 `server_error`。组件替换这几类请求的官方发送处理器：
调用方令牌已取消时取消照常传播，不记 Error；调用方令牌有效时的连接失败与超时与官方相同，记事件 6182 Error，并以同样的错误码、描述与 URI 拒绝。
保护作用于同一宿主内经 OpenIddict.Client 发出的发现、JWKS 与令牌请求，不含用户信息、内省、撤销等端点。

## 接口参考

| 入口或契约 | 作用 |
| --- | --- |
| `AddServiceAuthentication` | 全局工作负载注册 |
| `AddClientCredentials` | 命名客户端机器认证 |
| `AddTokenExchange` | 命名客户端用户委托 |
| `AddUserAccessTokenAccessor` | 当前请求的 Bearer 证明适配 |
| `AddAuthenticatedUserAccessTokenAccessor` | 指定可信认证票据的访问令牌适配 |
| `IUserAccessTokenAccessor.GetAccessTokenAsync` | 非 Web 宿主的证明来源接缝 |
| `AddServiceClient(serviceName, configure?, configSectionPath?)` / `AddServiceClientPipeline` | 手写客户端与标准管道 |
| `AddRefitServiceClient(serviceName, configure?, configSectionPath?, settings?)` | Refit 客户端、序列化与统一远端异常 |
| `ReadContentAsync` / `ReadResultAsync` / `EnsureRemoteSuccessAsync` | 裸载荷、信封互操作与错误读取 |

## 配置项

| 配置节 | 属性 | 说明 |
| --- | --- | --- |
| Leistd:ServiceAuth | Authority、ClientId、ClientSecret | 绝对签发者 URI、工作负载 ID、密钥，启动必填 |
| Leistd:ServiceClients:{Name} | BaseAddress | 可由宿主通过 HttpClient 构建器设置；没有地址时调用失败 |
| Leistd:ServiceClients:{Name} | Scope | 机器范围（空格分隔） |
| Leistd:ServiceClients:{Name}:TokenExchange | Audience、Scope | 用户委托目标，两者必填 |

```json
{
  "Leistd": {
    "ServiceAuth": { "Authority": "https://login.example.com/", "ClientId": "orders-api" },
    "ServiceClients": {
      "Identity": { "BaseAddress": "https://login.example.com/", "Scope": "tenant-routing.read" },
      "Billing": { "BaseAddress": "https://billing.example.com/", "TokenExchange": { "Audience": "billing-api", "Scope": "billing-api" } }
    }
  }
}
```

## 注意事项

- ClientSecret 由密钥管理或环境变量注入，不进入源码或已提交配置。
- 用户委托必须提供已验证的用户访问令牌，仅有会话身份不足以构成证明。
- 机器范围按命名客户端配置，令牌端点由签发者发现文档提供。
- 机器认证不自动传递环境租户；租户业务端点以路由或请求参数显式接收租户，并自行校验调用权限与租户有效性。
- `AddServiceAuthentication` 设置官方 OpenIddict.Client 的全局 `DisableTokenStorage`，同一宿主的交互式登录也会关闭 state 令牌存储。
  需要存储的宿主在所有组件注册之后调用 `services.Configure<OpenIddict.Client.OpenIddictClientOptions>(options => options.DisableTokenStorage = false)`，并接入官方 Core、令牌存储及交互式客户端所需的证书和宿主集成。
- 身份服务仍是交换与新机器令牌获取的依赖；热缓存可用不代表新令牌能获取成功。

## 相关

- [链路追踪](./tracing.md)
- [当前用户与身份信息](./security.md)
- [多租户](./multi-tenancy.md)
- [统一 API 响应](./response.md)
- [业务异常与全局异常处理](./exception-handling.md)
- [官方 Geonosis Token Exchange 示例](https://github.com/openiddict/openiddict-samples/tree/dev/samples/Geonosis)
