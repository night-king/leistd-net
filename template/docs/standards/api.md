# API 规范

## 1. 基本原则

- API 应表达资源和业务动作，避免暴露内部实现细节。
- 请求、响应、错误格式保持一致。
- 所有破坏性操作必须具备认证、授权、审计和幂等策略；认证与权限规则见 [认证与授权](auth.md)。
- 删除不存在的资源（本地或外部）视为成功：按 [§2.2](#22-空响应) 的空响应返回（业务接口 200，框架组件端点 204）、不写操作记录，见[通用约定 §3.6](./coding-common.md#36-删除幂等且保留真实失败)。

## 2. 响应格式

本项目响应风格：**成功直接返回业务对象（裸对象，不包裹），失败统一返回 RFC 9457 `ProblemDetails`**。
该风格由 Leistd 框架的全局异常处理（`Leistd.ExceptionHandling.AspNetCore`）落地，无需也不应在 Controller 里手动包裹 `Ok(...)` 或自定义 `{success,data}` 信封。

> JSON 序列化：属性 camelCase、忽略 null 值、枚举序列化为 camelCase 字符串且不收数字；查询串里的分类取值同样写 camelCase（见[通用约定 §3.8](./coding-common.md#38-枚举只在边界转换)）。

### 2.1 成功响应

直接返回业务对象，HTTP 200：Controller 返回 `Task<TOutputDto>`，无 I/O 时可同步返回 DTO。

```json
{
  "id": "0198f2a1-...",
  "name": "示例",
  "isActive": true,
  "creationTime": "2026-06-25T08:00:00Z"
}
```

### 2.2 空响应

无返回对象的操作（删除、启用/禁用、登出等）使用无泛型 `Task`，成功时返回 HTTP 200 且无响应体。普通业务 Controller 不使用 `IActionResult` 表达成功结果；只有下面两种情形才使用它：

1. 端点返回 `Challenge`、`SignIn`、`Redirect` 等**协议结果**（含 `/connect/*`，一种或多种）；
2. 端点返回**文件下载**（如 CSV 导出）：应用服务返回**字节与元数据**（内容、媒体类型、建议文件名），由 Controller 包成 `File(...)`，应用层不依赖 MVC 返回类型。

```text
HTTP/1.1 200 OK
```

框架组件自带的端点（设置、权限、操作记录、通知、租户与租户连接，映射在 `backend/src/CompanyName.ProjectName.Api/Hosting/ComponentEndpoints.cs`）按 Minimal API 惯例，无返回对象的写操作返回 **HTTP 204**。客户端把 200 空响应与 204 一样当作成功处理，不按状态码分支。

### 2.3 分页响应

分页查询返回 `PagedResult<T>`，HTTP 200。固定字段 `totalCount` + `items`。

```json
{
  "totalCount": 0,
  "items": []
}
```

### 2.4 错误响应

失败统一返回 RFC 9457 `ProblemDetails`，`Content-Type: application/problem+json`，由 `Leistd.ExceptionHandling.AspNetCore` 的全局异常处理（`BusinessExceptionHandler`）产出。除 RFC 标准字段外，所有失败都带 `traceId` 扩展字段；业务错误另带稳定错误码 `code`，公开文案在标准字段 `detail`。输入校验、未预期异常、上游故障等协议层失败的契约就是 HTTP 状态码（RFC 9457 §4），只带本地化 `title` 与 `traceId`（校验另带 `errors`），不带 `code` 与 `detail`。

```json
{
  "type": "urn:leistd:problem:business-error",
  "title": "Bad Request",
  "status": 400,
  "detail": "用户名 'admin' 已存在",
  "instance": "/api/v1/users",
  "code": "User:UsernameTaken",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

字段说明：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| type | string | 稳定问题类型 URI；校验与业务错误分别为 `validation-error`、`business-error`，其余按状态码使用 ASP.NET Core 默认值 |
| title | string | 与 status 对应的错误标题，按当前语言本地化（如 `请求无效`、`Not Found`） |
| status | number | HTTP 状态码 |
| detail | string | 业务错误面向用户的说明，按错误码本地化或回落为安全文案；协议层失败不带 |
| instance | string | 出错的请求路径 |
| code | string | **稳定错误码**：只出现在业务错误上，形如 `User:UsernameTaken`；也是本地化词条键 |
| traceId | string | 链路标识（W3C 格式，第二段是 TraceId，用于检索日志；无 Activity 时为请求标识）；业务关联标识在响应头 `X-Correlation-Id` |

> `GlobalExceptionOptions.IncludeExceptionDetails` 默认为 `false`；开启后仅额外输出 `stackTrace`，只用于本地调试，生产环境不开启。

### 2.5 验证错误响应

`[ApiController]` 自动模型校验和内部调用抛出的 `System.ComponentModel.DataAnnotations.ValidationException` 都返回 HTTP **400**，使用 `urn:leistd:problem:validation-error` 与 Leistd `errors` 对象数组。跨字段或用例规则失败抛 `BusinessException`，状态见 §4。

```json
{
  "type": "urn:leistd:problem:validation-error",
  "title": "Bad Request",
  "status": 400,
  "instance": "/api/v1/products",
  "errors": [
    {
      "detail": "名称不能为空",
      "field": "name",
      "code": "Product:NameRequired"
    },
    {
      "detail": "价格必须大于 0",
      "field": "price"
    }
  ],
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

## 3. HTTP 状态码

| 状态码 | 场景 |
| --- | --- |
| 200 | 查询或操作成功（含创建：本项目业务接口有响应体时直接返回 DTO） |
| 400 | 服务端认为是客户端导致的请求错误；包括结构、字段、协议参数和未登记状态的业务拒绝 |
| 401 | 未认证 |
| 403 | 无权限 |
| 404 | 资源不存在 |
| 409 | 状态冲突或幂等冲突 |
| 415 | 请求媒体类型不受支持 |
| 422 | 可选：内容语法成立，但无法按其指令处理；只在客户端需区分时按错误码显式映射 |
| 429 | 请求触发明确的频率限制 |
| 500 | 服务端异常 |
| 502 | 未处理的上游拒绝、无效或提前中断的响应；不透传远端状态 |
| 503 | 本服务连接上游失败，或明确知道所依赖能力暂时不可用 |
| 504 | 本服务等待上游响应超时 |

## 4. 异常与 HTTP 映射

后端只保留一个业务异常 `BusinessException(code, safeMessage, innerException?)`。各 API 业务模块只登记自己的非默认 HTTP 状态（不重复登记 400），由组合根汇总；框架组件的默认状态由组件在自己的 `AddXxx` 里登记，需要改时在组合根用 `MapCode` / `MapException` 覆盖：

| 来源 | 默认 HTTP | 说明 |
| --- | --- | --- |
| `BusinessException` 命中错误码映射 | 400 / 401 / 403 / 404 / 409 等 | 宿主按稳定业务语义精确决定 |
| 未命中的 `BusinessException` | 400 | 广义的客户端请求错误；防止新错误码意外变成稀有状态。错误码在构造时必填且不可变，没有 `WithCode` |
| DataAnnotations 自动校验 / `ValidationException` | 400 | 请求字段或结构不合法，返回 `errors` |
| 未捕获的 BCL/技术异常 | 500 | 只返回通用安全文案，细节记日志 |
| 框架判定的请求错误（请求体无法解析、请求体过大、内容类型不符、路由不存在、未认证、限流） | 400 / 413 / 415 / 404 / 401 / 429 | `/api` 下统一返回 Problem Details，只有状态码、本地化标题与 `traceId`，不带业务错误码；开发与生产环境一致 |
| `ServiceClientException` | 500/502/503/504（注册客户端时自动登记） | 映射规则见[服务间调用](./service-invocation.md) |

框架不根据 BCL 异常类型猜测为 400/503；认证和授权拒绝交给 ASP.NET Core 管道，不用业务异常模拟，授权拒绝只有 403 状态码、不带 `code`。以下例外用业务码：

<!--#if (LocalIdentity)-->
- 界面靠稳定码路由的拒绝：未完成两步验证设置（`Auth:TwoFactorSetupRequired`）。
<!--#endif-->
- 取决于请求体的附加权限：分配角色（`User:ManageRolesRequired`）。
<!--#if (RemoteTokenAuth)-->
- 成员被本服务禁用或没有成员行：403 带 `User:LocalAccessDisabled` / `User:LocalMemberMissing`，见[资源服务的成员启停](./auth.md#资源服务的成员启停)。
<!--#endif-->

`WithData("Name", value)` 为本地化文案的 `{Name}` 占位符传值，无论是否启用多语言都可保留。不提供 `WithDetails`：技术详情只进入 `InnerException` 和日志。

错误码不随是否启用多语言而变化，命名与放置：

- 采用 `模块:语义`（`User:*`、`Auth:*`、`OpenApp:*`、`Security:*` 等），前缀由一个模块独占，常量成员名与语义后缀一致。
- 一个模块的码集中在一个 `Errors/` 文件，放在用到其中任一码的最低层（Domain 或 Application）；`Domain/Shared` 只放真正跨模块的契约；组件错误码引用组件常量。
- 错误码是对外契约（变更见 §8），前端分支与状态映射都要有针对性测试。

公开文案的回显边界见[通用约定 §1](./coding-common.md#1-语言与敏感信息)；未预期 5xx 只展示通用文案与 `traceId`。
<!--#if (SpaFrontend)-->
前端如何展示 `detail`、何时按 `code` 或状态码分支见[前端错误处理](./coding-frontend.md#6-错误处理)。
<!--#endif-->

<!--#if (IncludeLocalization)-->
### 4.1 异常本地化

本项目已启用多语言（`--include-localization true`），异常按请求 `Accept-Language` 本地化。`Message` 与 `Code` 分工如下：

| 职责 | 载体 | 说明 |
| --- | --- | --- |
| 安全回落 | `Message`（构造参数） | 可读英文，资源未命中或未启用多语言时会直接给用户，不得含内部细节 |
| 身份 + 展示 | `Code`（构造参数） | 稳定机器契约，也是本地化词条键 |

- 资源 `Resources/{en,zh-CN}.json` 的 `texts` 段按错误码给出各语言文案（`en` 为默认/回落），占位符与 `WithData` 的键同名：`"User:UsernameTaken": "Username '{Username}' already exists."`。抛出写法见[后端开发规范 §3.4](./coding-backend.md#34-领域服务)。
- 全局处理器按 **`Code` 词条 → 安全 `Message`** 解析，本地化失败不改写 `code` 或 HTTP 语义。
- **未启用多语言时**会直接返回 `Message`，因此必须从抛出点就是安全、可展示的文案；原始技术异常放在 `InnerException` 中。
- DataAnnotations 校验消息同样随 culture 本地化（写法见[后端开发规范 §5](./coding-backend.md#5-命名与-dto)），参数校验与业务异常在同一请求下同语言。
- 资源键集合、占位符与错误码格式由 `scripts/check-i18n.py` 检查（见[测试规范](./testing.md)）。

**示例（`Message` 是安全英文回落，`Code` 给出稳定身份）**：

```csharp
// 资源不存在；该码在 API 组合根映射为 404
var user = await userRepository.GetByIdAsync(id, cancellationToken);
if (user is null)
    throw new BusinessException("User:NotFound", $"User {id} not found.")
        .WithData("Id", id);

// 请求字段校验由 DTO DataAnnotations 与 [ApiController] 统一产生 400 + errors
```
<!--#endif-->

## 5. 分页规范

分页查询使用 `offset/limit` 偏移分页（与 `PageRequest` 一致），不使用 `page/pageSize`。下表为基础默认值；业务 DTO 可以派生并覆写属性默认值或校验特性，接口须说明自己的边界。

业务列表遵循本节；组件列表以对应组件契约为准，租户、操作记录、通知的分页当前采用固定排序（创建时间倒序、同刻按 Id 倒序），不提供 `sorting`。

| 参数 | 类型 | 必填 | 默认值 | 说明 |
| --- | --- | --- | --- | --- |
| offset | number | 否 | 0 | 起始偏移量，从 0 开始；**小于 0 返回 400** |
| limit | number | 否 | 10 | 每页数量；**取值范围 1 ~ 1000，越界返回 400** |
| keyword | string | 否 | 空 | 搜索关键字 |
| sorting | string | 否 | 空 | Dynamic LINQ 排序表达式，如 `name asc`、`creationTime desc, id asc`；空白使用接口默认排序 |

后端使用 `System.Linq.Dynamic.Core` 的 `OrderBy(string)` 解析，不自写解析器或通用字段白名单，不放宽库的默认限制。属性名忽略大小写，支持嵌套属性路径和多字段排序；方向省略时升序。字段对应查询模型中可排序的属性，例如用户最近登录时间使用 `lastLogin.Time`。

前端控制表格可选列，并将显示字段转换为查询属性路径。业务确需限制敏感属性或其他排序规则时，在所属模块的 `Validators/` 编写验证器，显式注册并在查询执行前调用；前端配置不能替代这些业务约束。验证库生成的表达式时遍历整个排序选择器，覆盖嵌套成员、条件和方法参数。

应用服务调用 `Shared/Paging` 的 `OrderBySorting(input.Sorting)`：它只包住这一次解析，解析失败（语法错误、未知或不可访问的成员、非法方向）返回 400 字段错误（`errors[].field` 为 `sorting`，固定文案 `The sorting expression is not valid.`，不带业务码，不回显表达式，日志不附带异常）。解析成功但查询无法翻译或执行的（如未映射的计算属性）仍是服务端异常，返回安全的 500；不对 `ParseException` 做全局映射，其他来源的解析异常也照常 500。必要的业务限制由模块验证器返回业务错误，如用户凭据访问由用户验证器拒绝并返回 400。解析和业务验证先于计数及空结果返回。

默认排序在业务 DTO 的 `override Sorting` 中声明，用 `nameof` 引用实际排序对象的属性：实体查询引用领域实体（如 `nameof(User.CreationTime)`），投影查询引用查询模型；不引用输出 DTO。将省略、null 和空白归一化为默认值。通常按 `CreationTime desc` 展示最新记录；用户和开放应用采用该默认值，角色按业务排序号 `Sort asc`。前端默认状态及 Mock 与对应 DTO 一致。在调用方的全部排序键之后分别追加 `Id`、`Name/Id`、`ClientId`，为同值记录建立确定次序；这不保证并发修改时的快照一致性。Mock 仅覆盖界面实际使用的属性路径与方向组合，不模拟完整表达式语言，其余表达式按解析失败返回同形的 400。

输入 DTO 命名见[后端开发规范 §5](./coding-backend.md#5-命名与-dto)，返回体见 §2.3。

## 6. HTTP 方法与路由规范

对外接口统一以 `/api/v1/` 开头。

| 操作 | 方法 | 路由 | 后端方法名建议 |
| --- | --- | --- | --- |
| 分页查询 | GET | `/api/v1/{resource}` | `GetPagedListAsync` |
| 单个查询 | GET | `/api/v1/{resource}/{id}` | `GetAsync` |
| 创建 | POST | `/api/v1/{resource}` | `CreateAsync` |
| 更新 | PUT | `/api/v1/{resource}/{id}` | `UpdateAsync` |
| 局部更新 | PATCH | `/api/v1/{resource}/{id}` | `PatchAsync` |
| 删除 | DELETE | `/api/v1/{resource}/{id}` | `DeleteAsync` |

## 7. API 文档

仅在 API 需要供其他团队、客户端或外部使用者长期查阅时建立文档。先参考项目中最新的同类 API 文档，根据真实契约说明必要的路由、权限、请求、响应和错误语义；不复制固定章节模板。

## 8. 兼容性

- 新增字段默认向后兼容。
- 删除字段、修改字段含义、修改错误码属于破坏性变更。
- 破坏性变更必须明确迁移方案并获得相关使用者确认。
