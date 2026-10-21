# 操作记录

记录关键操作：什么人、在什么时间、做了什么、结果如何。业务显式调用记录器，框架补齐操作人、时间与链路标识，交给唯一的写入方：数据库存储（可查询、导出、归档），或结构化日志（只写出，不保存可回读的历史）。

## 何时使用

| 场景 | 用法 |
| --- | --- |
| 事后要能回答"这条数据是谁改的、凭什么改" | 在用例里调 `IOperationRecorder.RecordSucceededAsync` |
| 业务规则拒绝了一次操作 | 调 `RecordFailedAsync`；独立提交，业务随后回滚也留得住；写不进去只记日志，不会把 403 变成 500 |
| 权限不足在**授权阶段**就被拒（请求到不了应用服务） | 端点打 `[OperationRecordAction]`，在授权结果处理器里调一行扩展方法 |
| 授权通过后，控制器或组件端点被业务规则拒绝 | 同一个注解，紧接授权启用 `UseOperationFailureRecording()` |
| 管理界面要列表、筛选、导出操作记录 | 路由组上调 `MapOperationRecords(...)`；自定义路由或 DTO 时直接用 `IOperationRecordQueryService` |
| 仪表盘要按区间或按日/按小时统计记录数、涉及的人数（含已归档的记录） | `IOperationRecordReader.CountByBucketsAsync`，桶边界按业务时区由调用方算好 |
| 记录要有保留期（到期搬入归档表） | `AddOperationRecordRetention<TDbContext>()`，默认关闭 |
| 不要产品内的历史查询，记录交给日志采集链路（SIEM 等） | `AddOperationRecordsLogging()` 代替数据库存储；记录器与调用点完全不变 |
| 想知道某次请求的完整细节（入参、堆栈、耗时） | **不要往这里加字段**，按 `CorrelationId` 去请求日志里查 |
| 想追踪实体逐字段的变更前后值 | 本组件不做，用 EF 的变更追踪另行实现 |

查询、个人偏好这类高频写入与定时任务的例行心跳不记。登录成功与失败属于安全事件，应当记录；匿名的登录失败按被尝试的标识计数，只在阈值上采样或限频后记录，不逐次落库（匿名请求的边界见「注意事项」）。

## 安装

```bash
# 记录器与契约
dotnet add package Leistd.OperationRecords.Core

# EF Core 持久化
dotnet add package Leistd.OperationRecords.EntityFrameworkCore

# 结构化日志输出（与 EF Core 持久化二选一）
dotnet add package Leistd.OperationRecords.Logging

# 被拒与业务拒绝的补记（注解 + HttpContext 扩展）
dotnet add package Leistd.OperationRecords.AspNetCore
```

## 注册

```csharp
builder.Services.AddOperationRecordsEfCore<AppDbContext>();   // 内部已调用 AddOperationRecords() 与 AddOperationRecordQueries()
```

记录器只认一个写入方（`IOperationRecordWriter`），历史读取（`IOperationRecordReader`）与查询用例只由能回读的存储登记。
数据库存储与日志输出互斥，同时注册在注册期即抛错。

宿主签发的模拟登录 claim 用了别的名字时：

```csharp
builder.Services.AddOperationRecords(options => options.ImpersonatorIdClaimType = "act_sub");
```

在 `OnModelCreating` 中映射记录表：

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ConfigureOperationRecords();
}
```

**前置**（全部必填，漏一个在首次解析 `IOperationRecorder` 时才暴露），组件不代为注册：

```csharp
builder.Services.AddUnitOfWork();
builder.Services.AddUnitOfWorkEfCore();            // 存储经 IDbContextProvider 取上下文，失败记录独立提交
builder.Services.AddSingleton<IClock, UtcClockProvider>();   // Leistd.Core 不提供 DI 扩展；DDD 基础设施包已代为注册
builder.Services.AddMultiTenancyCore();            // ICurrentTenant：记录写进哪一层
builder.Services.AddAmbientContext();              // ICurrentUser：谁做的
builder.Services.AddCorrelationIdCore();           // ICorrelationIdProvider：哪条请求链路
```

保留期归档的逐库遍历（`ITenantDatabaseRunner`）同样由 `AddMultiTenancyCore()` 提供，不分库时只有宿主库。

映射查询端点；授权口径全部必填，漏配在映射时抛出：

```csharp
app.MapGroup("/api/v1/operation-records").MapOperationRecords(options =>
{
    options.ReadPolicy = "App.OperationRecords";
    options.ExportPolicy = "App.OperationRecords.Export";
    options.ExportAction = "operation-records.exported";   // 须已登记
});
```

启用保留期归档（需要后台作业调度器与分布式锁，见[后台作业](./background-jobs.md)）：

```csharp
builder.Services.AddOperationRecordRetention<AppDbContext>();
```

默认关闭；启用时必须同时给出保留天数，组件不提供默认天数。开关登记给调度器：关闭时任务照常排期、到点跳过且不记完成，作业状态显示为 `SwitchedOff`；经宿主设置打开后，下一次到点即生效：

```json
{
  "Leistd": {
    "OperationRecords": {
      "Retention": { "Enabled": true, "RetentionDays": 365 }
    }
  }
}
```

### 结构化日志输出

不需要产品内的历史查询时，用日志输出代替数据库存储。记录器、动作定义、失败补记与调用点相同，只换写出的去处：

```csharp
builder.Services.AddUnitOfWork();
builder.Services.AddLocalEventBus();               // 成功记录借工作单元的提交后阶段写出（宿主须启用拦截器织入）
builder.Services.AddOperationRecordsLogging();     // 记录器的四样前置同上
```

```json
{ "Logging": { "LogLevel": { "Leistd.OperationRecords": "Information" } } }
```

| 记录 | 写出时机 |
| --- | --- |
| 成功，有环境工作单元 | 事务**提交之后**（`AfterCommit` 阶段）；回滚、提交失败都不写出。嵌套的子工作单元归入外层；`requiresNew` 的独立工作单元在它自己提交后写出；非事务型工作单元在保存与 `BeforeCommit` 阶段成功之后写出；若之前失败，已落库的非事务写入不会回滚，也没有成功记录 |
| 成功，无环境工作单元 | 立即写出——调用方在变更落库之后才记录，与数据库存储"即时生效"同一调用约定 |
| 失败 | 立即写出，不等待、不跟随调用方的事务 |

- 常量命名空间为 `Leistd.OperationRecords.Logging.Constants`。日志类别 `OperationRecordLogging.CategoryName`（`Leistd.OperationRecords`）；成功为 `Information`（事件 7100），失败为 `Warning`（事件 7101）。字段与数据库存储逐一对应：`OperationRecordId`、`OperationAction`、`OperationOutcome`、`OperationTargetId`/`Name`、`OperationActorId`/`Name`、`OperationActorTenantId`、`OperationTenantId`、`OperationAuthorizationBasis`、`OperationVisibility`、`OperationImpersonatorId`/`Name`、`OperationFailureCode`/`Detail`、`OperationTime`（UTC）、`OperationCorrelationId`。失败参数例外：只记参数名 `OperationFailureDataKeys`，不记值——它是词条占位的原值，可能含提交者的邮箱等联系方式，按开发规范不进日志；需要参数原值的审计用数据库存储。
- 记录在调用时冻结：标识、时间、操作人与租户都在记录器里定案，提交后写出时不再读取任何上下文。
- 启动期校验：该类别对 `Information` 未开启，或缺少工作单元与本地事件总线时，宿主启动失败。
- 持久化保证弱于数据库存储：事务已提交、日志尚未写出时进程退出，这条成功记录会丢失；日志的保留期与防篡改由采集链路负责。
- 业务已提交之后日志写不出去时，不改变业务结果、不补记失败，在 `OperationRecordLogging.DeliveryCategoryName` 指定的类别以 `Critical`（事件 7102）报告动作码与记录标识。
- 不注册历史读取与查询：`MapOperationRecords` 在本模式下映射时即抛错，保留期归档也不适用。

## 使用

动作码、类别与授权依据由业务定义并保持稳定，框架只存不读；界面按动作码本地化：

```csharp
public static class OperationRecordActions
{
    public const string UserCreated = "identity.user.created";
    public const string UserDisabled = "identity.user.disabled";
    public const string PasswordChanged = "identity.user.password-changed";
}

/// <summary>动作的类别，驱动界面的分类筛选。</summary>
public static class OperationRecordCategories
{
    public const string Account = "account";
    public const string Authentication = "authentication";
}

/// <summary>不由权限把守的操作，凭什么放行。</summary>
public static class OperationRecordAuthorizations
{
    /// <summary>用户改自己的数据，凭的是"已认证且是本人"。</summary>
    public const string AuthenticatedSelf = "AuthenticatedSelf";

    /// <summary>机器主体凭 client credentials 令牌调用，凭的是该客户端被授予的 scope。</summary>
    public const string ClientCredentials = "ClientCredentials";
}
```

动作码必须登记类别与可见性，未登记的码写入时直接抛出：

```csharp
public class AppOperationActionDefinitionProvider : IOperationActionDefinitionProvider
{
    public void Define(IOperationActionDefinitionContext context)
    {
        context.Add(OperationRecordActions.UserCreated, OperationRecordCategories.Account, OperationVisibility.Tenant);
        context.Add(OperationRecordActions.UserDisabled, OperationRecordCategories.Account, OperationVisibility.Tenant);
        context.Add(OperationRecordActions.PasswordChanged, OperationRecordCategories.Authentication, OperationVisibility.Actor);
    }
}

builder.Services.AddSingleton<IOperationActionDefinitionProvider, AppOperationActionDefinitionProvider>();
```

用例里显式记录：

<!-- no-compile: 省略号代表与本例无关的参数和实现 -->
```csharp
public class UserService(IOperationRecorder recorder, ...)
{
    public async Task<User> CreateAsync(CreateUserInput input, CancellationToken ct)
    {
        var user = await userManager.CreateAsync(input, ct);

        // 落在调用方的事务边界里：业务回滚则这条记录一并回滚
        await recorder.RecordSucceededAsync(
            OperationRecordActions.UserCreated,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            "App.Users.Create",
            ct);

        return user;
    }

    public async Task DisableAsync(Guid id, CancellationToken ct)
    {
        if (user.IsLastAdministrator)
        {
            // 独立提交：下面的抛出让业务回滚，这条记录照样留下
            await recorder.RecordFailedAsync(
                OperationRecordActions.UserDisabled,
                OperationTarget.For(id, user.DisplayName ?? user.Username),
                "App.Users.Update",
                OperationFailure.FromCode("User:LastAdministrator"));
            throw new BusinessException("User:LastAdministrator", "The last administrator cannot be disabled.");
        }
        ...
    }
}
```

### 失败原因要带参数

拒绝记录应带上判定所用的值。参数走 `FromCode` 的第二个入参（键即资源文案里的占位名）；
`BusinessException.LocalizationData` 是同一类型，异常与记录可以用同一份参数：

<!-- no-compile: 省略号代表与本例无关的参数和实现 -->
```csharp
// 应用服务：业务规则拒绝，不是授权拒绝——授权那一档由端点上的
// [OperationRecordAction] 自动补记，这里是走到了业务逻辑之后才判定的
public async Task<OrderDto> PlaceAsync(PlaceOrderInput input, CancellationToken ct)
{
    var customer = await customers.GetAsync(input.CustomerId, ct);
    if (customer.Available < input.Amount)
    {
        // 独立提交：紧随其后的抛出让业务回滚，这条记录照样留下
        await recorder.RecordFailedAsync(
            OperationRecordActions.OrderPlaced,
            OperationTarget.For(customer.Id, customer.Name),
            PermissionConstant.Orders.Create,
            OperationFailure.FromCode(OrderErrorCodes.CreditLimitExceeded, new Dictionary<string, object?>
            {
                ["Limit"] = customer.CreditLimit,
                ["Available"] = customer.Available,
                ["Requested"] = input.Amount
            }));
                // 失败记录不接收取消令牌；成功路径的 RecordSucceededAsync 传 ct

        throw new BusinessException(OrderErrorCodes.CreditLimitExceeded, "Insufficient credit limit.")
            .WithData("Available", customer.Available)
            .WithData("Requested", input.Amount);
    }
    ...
}
```

失败原因与错误响应共用宿主资源里的同一条词条：键就是错误码，占位符是 `{Name}`：

```json
"Order:CreditLimitExceeded": "可用额度 {Available}，本次需要 {Requested}。"
```

查询时 `IOperationRecordQueryService` 按当前请求语言查这条词条、用 `FailureData` 填占位符，结果放在 `FailureMessage` 里下发。
词条缺失或宿主没有启用本地化时 `FailureMessage` 为空（不报错），界面回落显示裸码；没有对应参数的占位符原样保留。

审计需要与接口报错不同的措辞时，另备一条 `{码}:Record` 词条：查询先查它，取不到再查码本身。
两步各自走文化回落（请求语言 → 父文化 → 默认语言），审计键在整条回落链上都没有才用码本身，
因此只在默认语言备了 `:Record` 时，中文请求看到的是英文审计措辞。占位名与 `FailureData` 的键逐字一致（区分大小写）：

```json
"Auth:InvalidCredentials": "The username or password is incorrect.",
"Auth:InvalidCredentials:Record": "Incorrect username or password ({attempts} failed attempts within {windowMinutes} minutes)"
```

- 异常与记录用同一个码。已有 `BusinessException` 时直接 `OperationFailure.FromCode(exception.Code, exception.LocalizationData)`；
  不要带入 `Exception.Data` 与异常文本。`FailureData` 对租户读者可见、会进导出，抛异常时只把可公开展示的值放进 `WithData`。
- 参数只能是标量（字符串、布尔、数值、日期、`Guid`）；对象只写下类型名。
- 参数不要塞进 `FromDetail`：那一路是技术说明，不本地化。
- 只在真的拒绝时记，校验没通过就返回的情形不记。

用户自助操作没有权限名可填，传业务自己的授权依据标记：

```csharp
await recorder.RecordSucceededAsync(
    OperationRecordActions.PasswordChanged,
    OperationTarget.For(currentUser.Id!.Value, currentUser.Name ?? currentUser.Username),
    OperationRecordAuthorizations.AuthenticatedSelf,
    ct);
```

授权阶段的拒绝：端点声明动作码，宿主在自己的授权结果处理器里补记。

<!-- no-compile: 省略号代表与本例无关的参数和实现 -->
```csharp
[HttpPut("{id:guid}")]
[Authorize(Policy = "App.Roles.Update")]
[OperationRecordAction(OperationRecordActions.RoleUpdated, "id")]
public Task<Role> UpdateAsync(Guid id, ...) => ...;
```

示例省略了控制器上的类级 `[Authorize]`：命名策略不叠加宿主的默认策略。

打了注解就记，没打就不记，不按 HTTP 方法等启发式筛选。授权依据记实际未通过的具名策略：
端点叠了多个策略时逐个重新评估，取最后声明的未通过者。

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

public sealed class AuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler defaultHandler = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context,
        AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden)
        {
            await context.RecordDeniedOperationAsync();
        }

        await defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }
}
```

不传原因时补上 `OperationFailureCodes.Forbidden`（`Error:Forbidden`）。这是本组件唯一自产的失败原因码，
不出现在错误响应里；中英默认译文由 `AddOperationRecords()` 登记，宿主资源里的同名键覆盖它。

授权通过之后的业务拒绝，通过组件中间件补记并原样重抛：

```csharp
app.UseAuthorization();
app.UseOperationFailureRecording();
```

只处理 `BusinessException`，记录错误码与 `LocalizationData`，不记录异常文本或 `Exception.Data`。端点需有操作注解；匿名请求跳过，先行记录的失败按动作与目标去重。也可在自定义管道中调用 `RecordFailedOperationAsync`。

- 必须紧接 `UseAuthorization()`：此处授权已通过，且请求仍在租户作用域内。不要放进 `IExceptionHandler`：
  那时租户作用域已随异常退出，租户内的失败会写进宿主层，授权之前抛出的业务异常也会流到那里。
- 授权依据取端点最后声明的具名策略，不重新评估；没有具名策略时记 `-`。
- 只记业务拒绝；参数校验失败（`ValidationException`）与技术异常属于请求日志。
- 兜底记录只有从路由推出的目标标识，**不带目标名**；目标来自请求体（如创建类端点）时记 `-`。
  授权被拒发生在应用服务之前，这条路径始终没有目标名，这是有意的：回填名字会把无权访问的目标名写进记录。
- 授权通过后的业务拒绝需要目标名或请求体里的目标时，由应用服务在拒绝处调 `RecordFailedAsync`。
  本次请求里同一动作码与目标已记过时，兜底跳过，留下先记的那条（契约见 `RecordedFailureTracker`）；
  `RecordFailedAsync` 自身不去重，多次调用记多条。
- 注解声明的目标要与应用服务记录的目标逐字一致（含 `TargetIdPrefix`），否则一次失败记成两条。
- 路由段有 Guid 类型依据时归一为小写 `D` 格式，与 `OperationTarget.For(Guid)` 一致：控制器动作参数或 Minimal API 处理方法参数为 `Guid` / `Guid?`
  且从路由绑定（按绑定名匹配，含 `[FromRoute(Name = ...)]` 别名；未标来源的参数须与路由模板参数同名），或路由参数带 `guid` 约束。
  同名但显式取自查询、请求头、请求体、表单或服务的 `Guid` 参数不算依据。请求里写成大写或 `N` 格式也落同一个目标。
  没有类型依据的段（`string` 参数、无约束的自定义端点，以及经复杂类型属性或控制器属性绑定的 Guid）原样保留，
  32 位十六进制或 Guid 外观的字符串主键不会被改写；
  这类端点的应用服务须按路由原文记录目标。
- 兜底推不出目标（端点没声明目标路由键或某段缺失）时只按动作码判，同一动作的第二次失败不会被补记；
  要逐条留痕就由应用服务按目标逐条调用 `RecordFailedAsync`。
- 去重只在同一 DI 作用域内有效：本地事件分发与新开的非子工作单元会新建作用域，从那里解析的记录器会多记一条（不丢记录）。
  应用服务按构造注入拿记录器不受影响。
- 同一作用域内并发留痕（`Task.WhenAll`）须自行串行化。

## 接口参考

| 成员 | 说明 |
| --- | --- |
| `IOperationRecorder.RecordSucceededAsync(action, target, authorizationBasis, ct)` | 记录一次成功；数据库适配在有环境事务时随事务提交，日志适配在提交后输出。调用阶段异常照常上抛，日志提交后投递故障只报告。`Host` 可见的动作在租户上下文里调用即抛 `InvalidOperationException` |
| `IOperationRecorder.RecordFailedAsync(action, target, authorizationBasis, failure)` | 记录一次被拒或失败；独立提交、不接收取消令牌，写失败只记日志不抛 |
| `OperationTarget` | 目标的标识与名字快照捆绑传递；`For(id, name)` / `None` |
| `OperationFailure` | 失败原因；`FromCode(code, data)` 走本地化码（`data` 是标量字典），`FromDetail(detail)` 走技术说明。不提供接受 `Exception` 的工厂 |
| `OperationFailureCodes` | 本组件自己会写进记录的失败原因码，目前只有 `Error:Forbidden`（授权被拒的默认原因），不出现在错误响应里。中英默认译文随包分发，宿主资源的同名键覆盖它 |
| `OperationRecordInfo` | 一条记录的传输形态；各列长度上限以 `Max*Length` 常量给出。另带 `TargetName`（目标名快照）、`Visibility`（必填）、`ActorTenantId`（操作人自己所属的租户；匿名请求取请求所在的租户上下文），以及 `FailureCode`（本地化码）、`FailureData`（占位参数 JSON，不设长度上限）、`FailureDetail`（技术说明，仅宿主可见） |
| `OperationRecordOutcome` | `Succeeded`、`Failed`；只有两档。查询输出直接带枚举，JSON 写法交给宿主的枚举转换器（模板为 camelCase：`succeeded`、`failed`） |
| `OperationVisibility` | `Tenant` / `Host` / `Actor`；写入时取自动作定义。`Host` 表示记录属于宿主层，见「实现行为」 |
| `OperationSeverity` | `Info` / `Notice` / `Critical`；描述动作本身有多要紧，与结果好坏无关。筛选项里的 `Severity` 直接带枚举，JSON 写法同上 |
| `IOperationActionDefinitionProvider` | 登记本模块的动作定义；宿主用 `AddSingleton<IOperationActionDefinitionProvider, ...>()` 注册。类别是业务定义的字符串，框架不预置清单 |
| `IOperationActionDefinitionManager` | 动作定义的只读索引；`GetOrNull` 返回 `null` 即未登记。写入时记录器据此抛错；读取历史记录时调用方据此降级（原样显示裸码） |
| `OperationRecordOptions` | `ImpersonatorIdClaimType` / `ImpersonatorNameClaimType` 默认值取自 `CustomClaimTypes`；操作人标识按 `ClaimTypeOptions.UserIds` 读取（`ICurrentUser.SubjectId`），不在这里另配 |
| `IOperationRecordWriter.InsertAsync(record, ct)` | 写入；成功记录只在它描述的变更生效之后可见（有环境工作单元时跟随它），失败记录立即、独立于调用方事务写出。一个宿主只有一个写入方 |
| `IOperationRecordReader.GetPagedListAsync(criteria, page, ct)` | 只由可回读的存储实现；按创建时间倒序分页，只读未归档的记录，返回 `PagedResult<OperationRecordInfo>`；`OperationRecordCriteria` 的 `Scope` 必填，关键字匹配动作码、目标标识、目标名与操作人名，时间两端都是**闭区间**且按 UTC 比较；`PageRequest.Sorting` 不生效 |
| `IOperationRecordReader.CountByBucketsAsync(scope, edges, actions?, distinctTargets?, ct)` | 按调用方给定的 UTC 桶边界计数，**覆盖已归档的记录**：第 i 项是 [`edges[i]`, `edges[i+1]`) 内的记录数，结果长度为边界数减一，没有记录的桶为 0；边界上的记录只归后一个桶。边界至少两个且严格递增，否则抛 `ArgumentException`；桶可不等长（夏令时当天的日桶为 23 或 25 小时），组件不接收时区、不生成日历。`distinctTargets` 为 `true` 时每桶数不同目标标识（如登录成功的不同用户数），没有目标（`-`）的记录不计入，各桶分别去重；只传首尾两个边界即整段区间的总数。主表与归档表在一条语句里合并后按 `Leistd.Data` 的 `GroupByTimeBucket` 分组 |
| `OperationRecordCriteria` | 分页查询条件：`Scope` 必填；`Keyword`、`StartTime`、`EndTime`、`Actions`、`Outcome` 可选，时间两端为闭区间、按 UTC 比较 |
| `IOperationRecordQueryService` | 查询、筛选项与导出用例：无租户上下文即宿主读者；租户读者看不到 `Host` 层、`Actor` 层只看本人（`ActorId` 与 `ActorTenantId` 都与读者相同）；仅宿主字段（`FailureDetail`、`CorrelationId`、`ActorTenantId`）只下发给宿主读者；类别与动作维度间取交集，展开为空返回空页；`FailureMessage` 为按请求语言渲染的失败原因（导出另成 `FailureMessage` 列）。**不做权限判定**，由端点策略把守 |
| `MapOperationRecords(configure)` | AspNetCore 包：`GET /`、`GET /filter-options`、`GET /export`；`ReadPolicy`、`ExportPolicy`、`ExportAction` 必填；未注册查询用例（日志输出模式）时映射即抛错；返回路由组，端点名前缀见 `OperationRecordEndpoints.NamePrefix`。查询串 `outcome` 只收 camelCase 名称 `succeeded`、`failed`，其他大小写、数字与未定义值是字段 `outcome` 的 400 |
| `AddOperationRecordRetention<TDbContext>(configure?, configSectionPath?)` | EF 包：绑定 `configSectionPath`（默认 `Leistd:OperationRecords:Retention`）并启动期校验，校验消息按实际路径报键，重复调用换用另一配置节时抛出；登记集群周期任务 `operation-records.archive`（每日 `DailyRunHourUtc` 执行） |
| `IOperationRecordArchiveService` | EF 包：逐库、分批把到期记录搬入 `OperationRecordArchive`，每批一个事务；返回搬运条数与失败库数 |
| `OperationRecordVisibilityScope` | 可见范围，由调用方算好，只能从三个入口取得：`Host` 见全部，`ForTenantReader(actorId, actorTenantId)` 见租户层加本人的 `Actor` 层（标识与所属租户都相同才算本人），`Unrestricted` 不过滤（供不代表读者的内部任务，以及只出聚合数、不下发单条记录的统计）。存储不判定读者是否为宿主 |
| `AddOperationRecords(services, configure?)` | 注册记录器与动作定义，登记自产失败码的默认译文；`configure` 修改 `OperationRecordOptions`（不绑定配置节），重复调用时依次叠加；不注册写入方与查询 |
| `AddOperationRecordQueries(services)` | 注册 `IOperationRecordQueryService`；要求 `IOperationRecordReader`，由可回读的存储适配调用 |
| `AddOperationRecordsEfCore<TDbContext>(services)` | 注册 EF Core 存储（写入与读取）；内部调用 `AddOperationRecords()` 与 `AddOperationRecordQueries()` |
| `AddOperationRecordsLogging(services)` | Logging 包：注册结构化日志写入方，不注册读取与查询；启动期校验日志类别与工作单元前置 |
| `OperationRecordLogging` | Logging 包：日志类别 `CategoryName`、`DeliveryCategoryName` 与事件标识常量 |
| `ConfigureOperationRecords(modelBuilder)` | 映射 `OperationRecord` 与归档表 `OperationRecordArchive` |
| `IOperationActionDefinitionContext.Add(..., targetIsActor)` | 标记自证类动作：成功且没有操作人时，查询输出的 `ActorIsTarget` 为真，界面把目标显示在操作人列 |
| `[OperationRecordAction(action, params targetRouteKeys)]` | 声明写端点的动作码；`TargetIdPrefix` 可对齐成功路径的目标标识写法 |
| `HttpContext.RecordDeniedOperationAsync()` | 在授权结果处理器里补记一条失败；端点有注解才记，匿名请求（没有任何已认证身份）一律不记；授权依据取实际未通过的具名策略 |
| `UseOperationFailureRecording()` | 紧接授权、位于租户作用域内，补记带操作注解的端点业务失败并原样重抛 |
| `HttpContext.RecordFailedOperationAsync(failure)` | 在紧接 `UseAuthorization()` 的中间件里补记授权通过之后的业务拒绝；判据同上，授权依据取最后声明的具名策略；`failure` 为空抛 `ArgumentException`。业务异常传 `OperationFailure.FromCode(exception.Code, exception.LocalizationData)`，参数进审计与导出 |

## 实现行为

- 时间与 `TenantId` 由记录器填充，不依赖审计拦截器或 `BaseDbContext`；实体不实现 `ICreationAuditedObject`。
- 操作人名与目标名存快照，改名或销号后仍保留当时的名字。
- 操作人标识读 `ICurrentUser.SubjectId`（按 `ClaimTypeOptions.UserIds` 取 claim 原始值），机器主体（`client:<client_id>`）与后台作业主体也有标识。
  自证类动作（`targetIsActor`，如登录、注册）在匿名请求里完成，操作人取目标。
- 后台作业、消息消费者、Hub 调用不经 ASP.NET Core 中间件，须先用 `IAmbientContext.Begin(principal)` 建立环境上下文
  （只切主体的 `ICurrentPrincipalAccessor.Change` 不带租户与链路）。主体的 `sub` 由宿主自定前缀（如 `job:nightly-cleanup`），`name` claim 决定操作人名。
- 模拟登录时主体上的用户是被模拟者，真实操作人按 `OperationRecordOptions` 指定的 claim 读出，另存 `ImpersonatorId` / `ImpersonatorName`。
- 事务边界（数据库存储）：成功记录落在调用方所处的边界里，随业务回滚；失败记录在新开的工作单元里独立提交，业务回滚后仍保留。
  只允许单个写事务的数据库（如 SQLite）上，外层已有未提交写入时这次写入等锁直至超时，结果是一条 `Error` 日志、记录丢失。
  在 `AfterCommit` 处理器或无工作单元路径中记录时业务已提交，记录另行持久化；需要业务与记录原子持久化时在原事务内调用记录器。
- 失败路径写不进去只记 `Error`，不抛；写入不可取消。动作码与授权依据为空白抛 `ArgumentException`，动作码未登记抛 `InvalidOperationException`，这两项在失败路径同样抛出。
- 超长字段就地截断，截断前记 `Warning`。
- 可见性与记录所在的层：`Tenant` 与 `Actor` 的记录写进操作发生时的租户层。`Host` 的记录属于宿主层：租户上下文里的失败记录
  （如租户用户调用宿主接口被拒）改写进宿主层，来源租户记在 `ActorTenantId`；租户上下文里的成功记录直接抛错，这类动作应登记为 `Tenant`，
  或切到宿主上下文后再记。
- 索引：`(TenantId, CreationTime DESC)`、`(TenantId, Visibility, CreationTime DESC)` 与单列 `CreationTime`（保留期归档整库按时间扫）。
  关键字是对动作码、目标与操作人列的包含匹配，不单独建索引；按时间区间筛选能让它只扫区间内的行。
- 分页按 `CreationTime` 再按 `Id` 倒序，顺序确定；同一时刻内的先后取决于 Provider 的 Guid 比较方式。
- 计数把原表与归档表投影后 `UNION ALL`，筛选与聚合在一条语句里完成，归档作业同时搬运时不会重复或漏数。
  归档表按当前租户显式过滤（宿主上下文只取 `TenantId` 为空的行），`IDataFilter.Disable<IMultiTenant>()` 时与原表一同放开；
  可见范围与动作筛选在两张表上同样生效。两张表都按 `(TenantId, CreationTime)` 索引取区间，动作在区间内过滤，不另建动作索引。
- 失败原因存码、读时渲染：查询与导出用容器里的非泛型 `IStringLocalizer`（`AddJsonLocalization` 会注册）按码取文案，
  用 `LocalizationPlaceholders.Fill` 填占位符；`FailureData` 解析不了时按无参数渲染。导出的 CSV 保留码与参数两列，另加按导出请求语言渲染的 `FailureMessage` 列。
  CSV 的 `Outcome` 列固定写 camelCase 名称（`succeeded`、`failed`），与 HTTP 契约一致；它在 CSV 边界转换，不读宿主的 JSON 配置。
- `Outcome` 与 `Visibility` 以成员名（`Succeeded`、`Failed`）字符串落库，与 HTTP 契约的 camelCase 写法无关；改契约不迁移数据。表名沿用 EF Core 默认约定，不加框架前缀。

## 注意事项

- 动作码一旦发布就不要改：它是历史记录的含义本身。
- 存储没有更新与删除。保留期由 `AddOperationRecordRetention` 把到期记录搬入归档表；归档表不实现 `IMultiTenant`，为它开查询时须自行按租户过滤。
- 保留期默认关闭，启用时 `RetentionDays` 必填（30–3650），未填时启动失败；关闭时填了也校验区间。
- Minimal API 端点的查询参数不要改成 `[AsParameters]` 绑定：它把没有默认值的非空属性当必填，省略 `offset` 的请求会 400。
- `outcome` 查询参数也不要改成直接绑定枚举：查询串不经 JSON 枚举转换器，那样会拒绝 `succeeded`、放过 `0` 与未定义的 `999`。
  在别的入口直接调用查询用例时，入参的 `Outcome` 是可空枚举，未定义值按 `[EnumDataType]` 验证失败。
- 不要加请求维度字段（IP、UA、URL）或变更明细，按 `CorrelationId` 回查请求日志。
- `IOperationRecordWriter` 只能有一个实现：为第二个 DbContext 注册，或数据库存储与日志输出同时注册时，在注册期拒绝。
- 授权结果处理器由宿主实现；业务失败中间件由宿主显式调用 `UseOperationFailureRecording()`。
- 不要用“发布领域事件、由处理器统一订阅”取代记录器：授权阶段的拒绝没有领域事件；工作单元内拒绝并抛出时，事件随回滚丢失；授权依据只有调用点知道。
- 被拒记录只看注解，匿名请求一律不记：匿名请求没有操作人，记录会成为无需凭据的写入面。

## 相关

- [多租户](./multi-tenancy.md)
- [当前用户与身份信息](./security.md)
- [链路追踪](./tracing.md)
