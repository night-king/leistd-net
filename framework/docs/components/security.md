# 身份与会话安全

提供主体访问、请求信息、Cookie 票据、浏览器来源防护、一次码和可选 OpenIddict 集成。

## 何时使用

| 场景 | 使用 |
| --- | --- |
| 应用服务/领域服务中读取当前登录用户的 Id、用户名、角色、Claim | 注入 `ICurrentUser` |
| 机器客户端场景下识别调用方（ClientId） | 注入 `ICurrentClient` |
| 直接访问原始 `ClaimsPrincipal`，或在后台任务/测试中临时切换身份 | 注入 `ICurrentPrincipalAccessor` |
| 在领域/应用层使用平台中立的身份访问 | 引用 `Leistd.Security.Core` |
| 身份验证器、恢复码或短期验证码摘要 | 引用可选 `Leistd.Security.OneTimeCodes`，无 Web、Identity 或 DDD 依赖 |
| Cookie 浏览器 API 写请求和 WebSocket 来源检查 | 显式注册并启用浏览器来源防护 |
| OpenIddict 远端公钥轮换 | 引用 `Leistd.Security.OpenIddict.Validation` |
| OpenIddict 交换令牌期限或存储维护 | 引用 `Leistd.Security.OpenIddict.Server` |
| ASP.NET Core 宿主，需要从 `HttpContext` 取真实身份 | 引用 `Leistd.Security.AspNetCore` 并注册 |

## 安装

```bash
dotnet add package Leistd.Security.Core
dotnet add package Leistd.Security.AspNetCore
```

一次码能力单独安装 `Leistd.Security.OneTimeCodes`；其原生 DI/Options 依赖随包传递。

## 注册

在 Web 宿主的 `Program.cs` 注册：

```csharp
builder.Services.AddSecurity();
```

非 HTTP 宿主（后台作业、消息消费者）注册 `Leistd.Security.Core` 的入口即可：

```csharp
services.AddAmbientContext();
```

两者注册的内容：

| 接口 | `AddAmbientContext()` | `AddSecurity()` | 生命周期 |
| --- | --- | --- | --- |
| `ICurrentPrincipalAccessor` | `CurrentPrincipalAccessor`（只认显式建立的主体） | `HttpContextCurrentPrincipalAccessor` | Singleton |
| `ICurrentUser` | `CurrentUser` | 同左 | Transient |
| `ICurrentClient` | `CurrentClient` | 同左 | Transient |
| `IAmbientContext` | `AmbientContext` | 同左 | Transient |

`AddSecurity()` 就是在 `AddAmbientContext()` 之上把主体来源换成 `HttpContext.User`，
并注册 `IHttpContextAccessor`；两者的调用顺序无关。客户端信息可按需独立调用 `AddRequestClientInfo()`，不会随 `AddSecurity()` 自动登记。

`Begin(...)` 建立哪些维度取决于已注册的贡献者：租户维度随 `Leistd.MultiTenancy.AspNetCore`
分发，链路标识随 `Leistd.Tracing.Core`。没装的维度就是没有，不会给猜测值。

主体标识与租户的 claim 类型只在 `ClaimTypeOptions` 一处配置，框架各处都从这里读取。签发主体的宿主改了 claim 名时：

```csharp
builder.Services.Configure<ClaimTypeOptions>(options =>
{
    options.TenantId = "tid";                  // 默认 tenant_id
    options.UserIds = ["oid", "sub"];          // 默认 sub，其次 NameIdentifier
});
```

请求客户端信息由 `Leistd.Security.RequestContext.IRequestClientInfo` 提供，HTTP 宿主按需注册：

```csharp
builder.Services.AddRequestClientInfo();
```

默认实现为 Transient，调用时读取当前请求的 IP 与 User-Agent；无请求时两者为 null，空白 User-Agent 为 null。IP 取 RemoteIpAddress，宿主负责先执行转发头处理并配置受信代理。注册幂等，不覆盖宿主提供的接口实现。

### 服务端 Cookie 票据

浏览器票据可显式接入服务端存储：

```csharp
builder.Services.AddAuthentication("Session").AddCookie("Session");
builder.Services.AddDistributedTicketStore("Session");
```

宿主先提供 `IDistributedCache`、`IDataProtectionProvider` 和 `IDistributedLock`，并配置共享密钥环、应用名与缓存/锁实现。`Leistd.Security.AspNetCore` 因票据能力传递引用 `Leistd.Lock.Core`，不会替宿主选择这些实现。

入口登记单例 `ITicketStore`，保留宿主预先登记的实现；每个命名 Cookie 方案只挂载一次，配置委托按调用叠加。不同方案共用存储配置，Cookie 寿命、属性和原生事件由宿主决定。原生 callback、Events 子类与 EventsType 均保留，同请求同方案的事件共用实例。

组件在该方案的 PostConfigure 阶段包装 `Events`、`EventsType` 与 `CookieManager`：包装事件把各回调转发给原事件对象（EventsType 按请求从容器解析），登录时追加引用版本；包装 Cookie 管理器把版本写进浏览器引用。登记顺序：

| 配置 | 何时生效 |
| --- | --- |
| 整体设置 `Events`、`EventsType`、`CookieManager` | 在 `AddCookie`/`Configure` 或先于本入口登记的 PostConfigure 中设置，由组件包装 |
| 改 `Events.OnXxx` 回调 | 任何阶段均可；组件之后赋值的回调按原生语义取代该事件对原事件对象的转发，执行一次，登录仍生成新版本 |

接入方案启用启动校验：全部 PostConfigure 之后，`Events` 须仍为组件包装、`EventsType` 须为 null、`CookieManager` 须仍为组件包装，否则 `Host.StartAsync` 抛 `OptionsValidationException`，消息点名方案与属性。未接入的方案不校验；替换 `ITicketStore` 不受此限制。

`DistributedTicketStoreOptions` 默认绑定 `Leistd:Security:Tickets`，也可传 `configSectionPath`；先绑定后应用委托，启动校验：

| 属性 | 默认值 | 约束 |
| --- | --- | --- |
| KeyPrefix | Leistd:AuthTicket: | 非空白 |
| FallbackLifetime | 5 分钟 | 正值；仅在票据未指定到期时间时使用 |

默认实现将票据与 OAuth 令牌保护后存入缓存，浏览器仅持引用。显式再次登录换引用版本，旧引用不能读取或撤销新票据；滑动续期不能复活已删除或到期票据。读写和删除传递取消令牌，持锁操作同时响应失锁取消。

取消照常以 `OperationCanceledException` 传播，唯一例外是请求已中止时的被动续期：原生处理器在响应启动回调里续期，异常逃出后由服务器记为应用错误。调用方令牌与 `RequestAborted` 均已取消、且本请求不是显式登录时，续期不再进行，按原票据版本写出引用后正常返回；缓存写入可能已完成也可能未开始，下次请求能否续期取决于票据那时是否仍有效。显式登录、无 `HttpContext` 的服务端调用、请求仍在时的失锁与缓存或锁超时照常抛出。

票据被删除或到期后，浏览器仍带的旧引用只等同未认证（与原生 SessionStore 返回 null 一致），组件不下发删除 Cookie：在途旧请求的删除响应可能晚于重新登录到达，会删掉新 Cookie。重新登录直接覆盖旧引用。缓存读取抛出的异常（连接、超时等）按失败传播，不当作票据缺失降级为匿名；只有缓存中不存在、已到期或解保护失败的条目视为缺失。

直接服务端重载面向可信调用方；HTTP 读取、续期与删除校验 Cookie 引用版本；显式登录由原生事件标记。`DistributedTicketStore.TicketKeyProperty` 保存本机制的缓存键，需要它的宿主替换实现须提供相同元数据。数据保护用途固定，密钥环隔离由宿主的应用名负责。

### 验证码摘要

```csharp
builder.Services.AddVerificationCodeDigest();
```

默认 `IVerificationCodeDigest` 为单例 HMAC-SHA256 实现，可由宿主预先注册替换。`VerificationCodeOptions` 绑定 `Leistd:Security:VerificationCodes`，也可指定 `configSectionPath`；先绑定再应用配置委托。`Key` 是 Base64 格式、至少 32 字节的稳定服务端密钥，各副本和重启使用同一值。非空无效配置启动即失败，消息指明传入配置节。

缺失密钥允许解析服务，默认实现使用时抛出配置异常；宿主决定功能何时启用并补充“启用时必需密钥”的启动或业务设置验证。组件不生成回落密钥，不读取业务功能开关。

### 浏览器来源防护

`Leistd.Security.AspNetCore` 提供可选来源门禁，不随 `AddSecurity()` 开启。宿主提供原生 CORS，选择保护路径：

```csharp
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins("https://frontend.example.com").AllowCredentials()));
builder.Services.AddBrowserOriginProtection(options =>
{
    options.WritePaths = ["/api"];
    options.AllMethodPaths = ["/hubs"];
});
```

在受信转发头、Routing 与 CORS 之后启用；原生认证/授权和错误响应仍由宿主负责：

```csharp
app.UseRouting();
app.UseCors();
app.UseBrowserOriginProtection();
```

先调用 `AddBrowserOriginProtection()`；缺少注册时 `UseBrowserOriginProtection()` 抛配置异常。该入口以当前 `IApplicationBuilder.Properties` 标记防止重复挂载，重复调用返回同一构建器。放在 Routing 之前无法读取端点 CORS 元数据，宿主须保证上述顺序。

`BrowserOriginProtectionOptions` 默认绑定 `Leistd:Security:BrowserOrigins`，支持配置委托及 `configSectionPath`；先绑定后应用委托，启动验证：

| 属性 | 默认 | 契约 |
| --- | --- | --- |
| WritePaths | 空数组 | 非安全方法的路径前缀；GET/HEAD/OPTIONS/TRACE跳过 |
| AllMethodPaths | 空数组 | 所有方法的路径前缀，包括 WebSocket 握手 |
| CorsPolicyName | null | 无端点策略时使用的原生策略名；null使用默认策略 |

至少选择一个路径；前缀以 `/` 开头，除 `/` 本身外不能以 `/` 结尾，无空白、查询、fragment 或反斜线。按原生路径段匹配，`/api` 不匹配 `/api-other`；`/` 匹配所有路径。

单值 http(s) Origin 只接受本源或原生 CORS 的显式、允许凭据、非通配许可。`Origin: null`、多值和非法来源拒绝；端点禁用 CORS、内联策略或命名策略优先于默认。无 Origin 时仅 `Sec-Fetch-Site: same-origin/none` 放行，其余值或多值拒绝；两头均缺的非浏览器客户端放行。Origin 拒绝不能被 Fetch Metadata 覆盖。

Authorization 或成功 Bearer 均不豁免。跨源浏览器 Bearer 写请求也须得到允许凭据的 CORS 许可，否则403；模板部署通过 `Cors:AllowedOrigins` 明确允许源。Hub 选择 `AllMethodPaths`，CORS 本身不限制 WebSocket。拒绝直接返回403，由宿主状态码管道补充错误正文。此入口不发行防伪令牌，也不替代原生认证或表单 antiforgery；代理信任、外部源和部署由宿主确定。[微软 WebSocket 指南](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0#websocket-origin-restriction)。

### OpenIddict 集成

两个包均不依赖 Web、EF 或 DDD。`Validation` 只传递原生 Validation/SystemNetHttp；`Server` 传递原生 Server/Core 与 `Leistd.BackgroundJobs.Core`，不替宿主选择存储、传输或排期。应用主体工厂直接使用原生 Abstractions，业务权限和 scope 目录留在应用。

远端宿主先注册原生 Validation 和 SystemNetHttp，再调用：

```csharp
builder.Services.AddSigningKeyRefresh();
```

宿主在 runtimeconfig 中显式启用 `Switch.Microsoft.IdentityModel.UpdateConfigAsBlocking`；测试宿主也须设置，组件启动时验证此开关，不修改进程状态。配置节默认 `Leistd:Security:OpenIddict:SigningKeys`，入口先绑定、后应用委托，支持 `configSectionPath`：

| 属性 | 默认 | 校验 |
| --- | --- | --- |
| FetchTimeout | 10 秒 | 正值且不超过 HttpClient 支持的最大毫秒数；同时约束原生 HTTP 与当前请求等待 |
| MinimumInterval | 1 分钟 | 正值；按副本限制显式刷新，自动刷新仍交给原生配置管理器 |

未知 `kid` 的可读 JWS 在当前请求内刷新动态公钥；保留显式静态键。公钥只来自配置的签发方，不采用令牌中的 `iss/jku/x5u`。已有主体、JWE、不可读或无 `kid` 的令牌跳过；签名、issuer、audience 和期限仍由原生管道验证。抓取失败沿用缓存，取消向调用方传播；当前刷新结果替换旧动态键。原生刷新间隔及组件限频仍可能推迟新键生效，签发方应先发布公钥再切换签名。

默认时间源通过 `TryAdd` 注册，可由宿主提供 `TimeProvider`。重复注册只挂载一次原生处理器和限频包装，配置按调用叠加；宿主可经原生事件和配置 API 替换机制。

签发宿主先配置原生 Server，再按需调用：

```csharp
builder.Services.AddTokenExchangeExpirationLimit();
builder.Services.AddOpenIddictPruning();
builder.Services.AddRecurringJob<OpenIddictPruningJob>(OpenIddictPruningJob.Name,
    RecurringJobSchedule.DailyAt(new TimeOnly(3, 30)), RecurringJobScope.Cluster);
```

期限约束在原生签发主体准备后执行，只限制交换令牌到期不晚于源令牌；不决定交换权限。清理入口只登记参数和默认任务，不自动排程。宿主配置原生 Core/Store、调度器与时间源，也可登记自己的 `IRecurringJob`。

`OpenIddictPruningOptions.MinimumRetention` 默认14天、至少10分钟，配置节默认 `Leistd:Security:OpenIddict:Pruning`；支持配置委托和自定义节，启动验证。任务名为 `security.openiddict.prune`，按 UTC 当前时间减保留期，先调用令牌、再调用授权管理器的原生 `PruneAsync`，传递取消。记录删除判据由原生管理器和存储负责。

## 使用

注入 `ICurrentUser`，直接读取强类型属性与方法：

```csharp
public class OrderService(ICurrentUser currentUser)
{
    public Task PlaceOrderAsync()
    {
        if (!currentUser.IsAuthenticated)
            throw new UnauthorizedAccessException();

        Guid? userId = currentUser.Id;
        string? username = currentUser.Username;
        return Task.CompletedTask;
    }
}
```

OAuth2 client credentials 场景识别调用方客户端：

```csharp
public class ReportService(ICurrentClient currentClient)
{
    public void Export()
    {
        if (currentClient.IsAuthenticated)
        {
            string? clientId = currentClient.ClientId;
        }
    }
}
```

后台任务或测试用 `IAmbientContext` 同时建立已注册的主体、租户与链路维度：

```csharp
using System.Security.Claims;

public class SystemJob(IAmbientContext ambientContext, ICurrentUser currentUser)
{
    public void Run(ClaimsPrincipal systemPrincipal)
    {
        using (ambientContext.Begin(systemPrincipal))
        {
            _ = currentUser.Id;
        }
    }
}
```

非 HTTP 宿主注册 `AddAmbientContext()` 即可（见[注册](#注册)）；`ICurrentPrincipalAccessor.Change`
仍然可用，但只切主体这一维。

### 身份验证器与恢复码

```csharp
var secret = Totp.GenerateSecret();
var manualKey = Totp.FormatSecret(secret);
var uri = Totp.BuildUri("Example", "user@example.com", secret);
var now = DateTimeOffset.UtcNow;
var code = Totp.ComputeCode(secret, Totp.TimeStepAt(now));
long? usedStep = Totp.Verify(secret, code, now, lastUsedStep: null);
var recoveryCodes = RecoveryCodes.Generate();
var recoveryDigest = RecoveryCodes.Hash(recoveryCodes[0]);
```

TOTP 使用 RFC 6238 的 HMAC-SHA1、30 秒步长和 6 位码，容忍前后各一步；默认生成 20 字节随机密钥。`Verify` 返回命中的步序号，格式错误、不匹配或不大于 `lastUsedStep` 时返回 null。时间使用 `DateTimeOffset`，调用方显式提供；空密钥、负时间步等编程错误抛 BCL 异常。

`FormatSecret` 导出大写无填充 Base32，`ParseSecret` 容忍大小写、空白、分隔符和正确尾填充，非法或不完整编码返回 null；Base32 实现不作为公共文本工具发布。`BuildUri` 编码密钥并转义发行方与账号。

恢复码每组 10 个，每个为 16 个随机 Base32 字符（80 位），按 4 字符分组；`Hash` 去掉空白和分隔符、转小写后计算 SHA-256。调用方保护 TOTP 密钥、只展示一次恢复码明文，并原子保存已用时间步或消费摘要。组件不存储使用记录、不限制业务尝试次数，也不接管事务。

验证码摘要使用带密钥 HMAC-SHA256，`Matches` 按固定时间比较；格式损坏摘要返回 false。验证码有效期、用途、租户/收件人绑定、尝试次数与原子消费仍由宿主实现。[RFC 6238](https://www.rfc-editor.org/rfc/rfc6238)、[RFC 4648](https://www.rfc-editor.org/rfc/rfc4648)。

## 接口参考

### `Leistd.Security.Users.ICurrentUser`

| 成员 | 说明 |
| --- | --- |
| `IsAuthenticated` | 当前主体是否已认证：任一身份已认证即为 `true`（与官方授权管线判定"已认证用户"一致），无主体时为 `false`。标识、名字、租户仍只取自带标识的主体身份 |
| `SubjectId` | 主体标识原始值，按 `ClaimTypeOptions.UserIds` 读取；机器主体是 `client:<client_id>`。审计等"任何主体都要留得下标识"的场景用它 |
| `Id` | 在 `SubjectId` 之上只接受 `Guid` 的自然人用户 Id；机器主体等非 GUID 标识为 `null` |
| `TenantId` | 所属租户，按 `ClaimTypeOptions.ReadTenant` 读取；宿主用户返回 `null`，租户 claim 非法时抛 `InvalidOperationException`（不当作宿主）。这是主体 claim 的直读值，运行时权威租户上下文是 [多租户组件](./multi-tenancy.md) 的 `ICurrentTenant` |
| `Username` | 依次取 `preferred_username` / `name` / `Name` claim，均无返回 `null` |
| `Name` | 标准身份名称，依次取 `name` / `Name` claim，不回退 `given_name` |
| `Email` | 邮箱，依次取 `email` / `Email` claim |
| `FindClaim(claimType)` | 指定类型的第一个 `Claim`，跨全部身份查找（官方 `ClaimsPrincipal.FindFirst`），不存在返回 `null` |
| `FindClaims(claimType)` | 指定类型的全部 `Claim`（多值的角色、scope、amr），与 `FindClaim` 同一范围；没有主体时为空 |
| `IsInRole(role)` | 当前用户是否属于该角色：只看主体身份，按其 `RoleClaimType` 精确匹配；没有带用户标识的身份时按整个主体判断 |

`Username`、`Name`、`Email` 只在主体身份（`ClaimTypeOptions.FindSubjectIdentity`）上读取，与 `SubjectId`、`TenantId` 同源：主体身份没带 `name` 时，不会取到其他认证身份上的名字。主体上没有带用户标识的身份时按整个主体读取。

### `Leistd.Security.Clients.ICurrentClient`

| 成员 | 说明 |
| --- | --- |
| `IsAuthenticated` | `ClientId` 非空时为 `true` |
| `ClientId` | 取 `client_id`（`CustomClaimTypes.ClientId`）claim |

### `Leistd.Security.Claims.ICurrentPrincipalAccessor`

| 成员 | 说明 |
| --- | --- |
| `Principal` | 当前 `ClaimsPrincipal?`：优先返回 `Change` 显式设置的主体，否则回退到底层认证源 |
| `Change(principal)` | 临时切换主体，返回 `IDisposable`；`Dispose` 时恢复上一个主体，支持嵌套；传 `null` 抛 `ArgumentNullException` |

### `Leistd.Security.Claims.CustomClaimTypes`（静态常量）

| 成员 | 值 | 说明 |
| --- | --- | --- |
| `Subject` | `sub` | 主体标识（OIDC 标准）：自然人是 GUID 用户 Id，机器主体带 `client:` 前缀（见下文 `ClientSubject`）；审计等需要任何主体都留下标识的场景读它的原始值 |
| `ClientId` | `client_id` | OAuth2/OIDC 客户端标识符 |
| `SessionId` | `sid` | 会话标识符（OIDC 标准） |
| `IdentityProvider` | `idp` | 身份提供者（如 github / google / microsoft） |
| `IsSuperAdmin` | `is_super_admin` | 是否超级管理员（权限授权的超管判定约定来源） |
| `TenantId` | `tenant_id` | `ClaimTypeOptions.TenantId` 的默认值。读写租户 claim 一律经 `ClaimTypeOptions`，不直接用这个常量 |
| `ImpersonatorUserId` | `impersonator_id` | 模拟登录时真实操作人的用户 Id；非模拟场景没有此 claim |
| `ImpersonatorUserName` | `impersonator_name` | 模拟登录时真实操作人的显示名快照；非模拟场景没有此 claim |

标准字段直接使用 `System.Security.Claims.ClaimTypes`。

### `Leistd.Security.Claims.ClientSubject`（机器主体 `sub` 契约）

OAuth2 client credentials 令牌代表工作负载。其 `sub` 必须使用 `client:` 前缀，与可解析为 GUID 的用户 `sub` 隔离，避免机器主体被误认为用户。

| 成员 | 说明 |
| --- | --- |
| `Prefix` | 机器主体 `sub` 的前缀，常量 `client:` |
| `Format(clientId)` | 由 `client_id` 构造机器主体 `sub`（签发端使用） |
| `Matches(subject, clientId)` | 判定 `sub` 是否正是该 `client_id` 的机器主体（消费端使用） |

```csharp
identity.AddClaim(new Claim("sub", ClientSubject.Format(request.ClientId!)));

if (ClientSubject.Matches(principal.FindFirst("sub")?.Value, clientId)) { /* 受信的服务调用 */ }
```

> 多个认证方案合并主体时仍遵守该契约；用户访问令牌的跨服务使用见[服务间调用客户端](./service-client.md)。

### `Leistd.Security.Claims.ClaimsPrincipalExtensions`（是否匿名）

| 成员 | 说明 |
| --- | --- |
| `HasAuthenticatedIdentity()` | 主体的任一身份已认证即为 `true`，`null` 为 `false`；与官方授权管线一致，框架各处判断匿名都用它。不要用只看第一个身份的 `ClaimsPrincipal.Identity.IsAuthenticated` |

### `Leistd.Security.Claims.ClaimTypeOptions`（claim 类型与读取规则）

| 成员 | 说明 |
| --- | --- |
| `UserIds` | 主体标识的读取顺序，默认 `sub`，其次 `ClaimTypes.NameIdentifier` |
| `TenantId` | 租户 claim 类型，默认 `tenant_id`；值必须是租户 GUID，没有即宿主 |
| `FindSubjectIdentity(principal)` | 主体身份：按顺序第一个带用户标识（按 `UserIds`）的身份；没有时为 `null`。标识、租户、名字、邮箱都取自它 |
| `FindUserId(principal)` | 在主体身份上按 `UserIds` 取第一个非空白的原始值 |
| `ReadTenant(principal)` | 返回 `TenantClaim`，租户取自主体身份。非法：同一身份内多条（即使值相同）或非 GUID；其他带用户标识的身份带着与主体身份不同的租户（含主体身份为宿主）。不带用户标识的补充身份只在主体身份没有租户时提供租户；带用户标识而无租户 claim 的其他身份不参与判定；各身份带同一租户合法 |

`ICurrentUser.Id` 在原始值之上只接受 GUID；审计、SignalR 寻址等场景读原始值。

```csharp
bool isNaturalPerson = Guid.TryParse(claimTypes.Value.FindUserId(principal), out _);
```

## 注意事项

- 各属性在缺失对应 claim 时返回 `null`（`Id` 在 claim 无法解析为 `Guid` 时同样返回 `null`），调用方需做空值处理。
- 业务代码判断当前用户的角色用 `ICurrentUser.IsInRole`（只看主体身份）；授权策略 `RequireRole` 与官方 `ClaimsPrincipal.IsInRole` 看整个主体。两者都只认身份的 `RoleClaimType`、角色名区分大小写。自行构造 `ClaimsIdentity` 时，`roleType` 要与写入角色 claim 的类型一致（如 OIDC 的 `role`），否则判定静默为 `false`。
- `Change(...)` 基于 `AsyncLocal` 支持异步传播和嵌套，但返回的 `IDisposable` 必须释放。
- `HttpContextCurrentPrincipalAccessor` 依赖 `IHttpContextAccessor`，在没有 HTTP 上下文的后台任务里 `Principal` 为 `null`；此类场景用 `IAmbientContext.Begin(...)` 显式建立系统主体。
- 领域层/应用层使用 `Leistd.Security.Core` 与按需的 `Leistd.Security.OneTimeCodes`，避免引入 ASP.NET Core 集成包。
