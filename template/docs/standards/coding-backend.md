# 后端开发规范

本项目后端编码规范，同时遵循 [项目通用约定](./coding-common.md)。接口契约见 [API 规范](./api.md)，认证与权限侧别见 [认证与授权](./auth.md)。`Leistd.*` 的 API 以实际还原版本的包内文档与 XML 为准，定位方法见 [后端说明](../../backend/README.md#leistd-框架-api)。

## 1. 技术栈

.NET 10、EF Core 10、PostgreSQL、Redis（多实例部署）、Mapster（`Leistd.ObjectMapping.Mapster` + `IObjectMapper`）、Serilog。精确版本以项目文件为准。

## 2. 分层与目录

依赖方向：Domain ← Application ← Api；Infrastructure → Domain；Api 是组合根。Application 不引用 Infrastructure 与 `Microsoft.EntityFrameworkCore`。

| 层 | 职责 | 不做 |
| --- | --- | --- |
| Api | 路由、鉴权、调用应用服务、宿主组装 | 业务校验与编排、直接操作实体或仓储；HTTP 信息不传入 Application |
| Application | 编排用例：收发 DTO、调用领域对象与领域服务、查询与组装输出、入口相关的跨聚合校验与协调（见[通用约定 §3.2](./coding-common.md#32-校验按规则归属分层)）、事务边界、发布事件 | 核心业务规则 |
| Domain | 实体行为、领域服务、业务规则；定义仓储与第三方服务接口 | 引用 EF Core、DTO 转换、供展示的查询 |
| Infrastructure | 持久化、实体配置、外部适配器及其 Options | 业务规则 |

与宿主实体、业务政策无关的通用机制由框架组件提供；模板保留业务模型、组件扩展点适配和宿主组合，不另建平行技术实现。

目录按功能模块组织，类型目录是模块下的一级目录（如 `Settings/AppServices`、`Settings/Dtos`），不嵌进子功能目录；子功能目录（如 `Auth/Sessions`）只放不属于这些类型的协作类型，按职责命名（`*Store`、`*Verifier`、`*Factory`、`*Guard`），`*Service` 只用于应用服务与领域服务。

分类优先采用已有职责目录（DTO、应用服务、事件处理器、后台任务、映射、验证器等）。各层没有专属分类的框架扩展点实现放所属模块的 `Provider/`，必要的配套定义可与实现共置（如实时资源与权限对应表）。未分类的少量协作类型可留模块根（如 `OpenApplications/OpenApplicationQueryItem`）；不预建空目录或没有实际职责的层级。

- **Application**：`AppServices`（接口与实现）、`Dtos`、`Mappings`、`Errors`、`Events`（应用层发布、不来自实体的事件）、`EventHandlers`、`BackgroundJobs`，按需 `Constants`、`Abstractions`（由宿主实现的端口）、`Provider`（框架扩展点实现）、`Policies`、`Options`（仅应用用例消费的配置）。跨模块共用、不属于任何模块的约定（认证方案名、分页）放 `Shared/`，不作兜底目录。
- **Domain**：`Entities`（实体与聚合）、`ValueObjects`（不可变值类型，含有限状态枚举）、`DomainServices`、`Events`（实体发出的事件）、`Policies`、`Errors`、`Options`（仅领域自身消费的配置）、`Abstractions`（端口及其输入输出模型）、`Repositories`（聚合的自定义仓储接口）。不认识任何实体的领域共享能力按语义放 `Shared/`，它不是兜底目录；子目录名不与常用 BCL 类型同名。
- **Infrastructure**：持久化放 `Persistence/`，自定义仓储实现放其 `Repositories/`；外部适配器按功能归位，自己绑定和校验客户端标识、密钥、回调地址，Application 只依赖内层端口暴露的能力。租户连接扩展点实现放模块内的 `TenantConnections/Provider`。

Client 与 DbMigrator 的职责分类及根目录例外见 [§9](#9-client-与-dbmigrator-目录)。

领域服务之间只允许单向依赖，且仅用于复用另一个领域服务的**变更行为**，在类上注释原因；读取不跨领域服务调用。依赖环由 `ValidateOnBuild` 检出（见 §4）。

## 3. 编码

### 3.1 语言特性

| 特性 | 场景 |
| --- | --- |
| 主构造函数 | 服务类 |
| `record` + `init` | DTO；输出与内部程序载荷的必填成员加 `required` |

HTTP 输入模型（Controller 绑定的请求体与查询 DTO、交给框架端点绑定的派生入参）的必填字段写 `[Required]`，不用 `required`：`required` 让缺键在反序列化阶段失败，错误落在 `$` 上而不是字段上。引用类型写 `= null!`；值类型声明为可空（`Guid?`、`bool?`）再加 `[Required]`，否则缺键静默得到默认值，验证通过后才取 `.Value`。可选字段保持原有默认值，不借此改成必填。
例外是"必须出现、可以为 null"的存在性字段（如并发版本 `ExpectedVersion`，`null` 表示首次登记）：保留 `required` 或 `[JsonRequired]` 约束键的存在，缺键在解析阶段拒绝、没有字段级错误；`[Required]` 会把显式 `null` 一并拒掉，不能替代。

异步：I/O 一律 `async/await`，方法名以 `Async` 结尾（协议规定的名字除外），不用 `.Result`/`.Wait()`。

### 3.2 时间

- 业务代码注入 `Leistd.Timing.IClock`（`clock.Now`，恒为 UTC）。不就地读取 `DateTime.Now`/`UtcNow`、`DateTimeOffset.Now`/`UtcNow`、`TimeProvider.System.GetUtcNow()`：它们隐藏依赖、不可测。
- ASP.NET Core 认证、Cookie、票据等框架集成回调要求 `TimeProvider` 时可注入 `TimeProvider`。
- 实体不注入服务：时间相关方法接收 `DateTime now`，由调用方传入。
- 契约里的时间字段写明单位（秒或毫秒）。

### 3.3 实体

属性 `private set`；保留 EF Core 用的 `private` 无参构造；状态只经公共方法修改；构造函数做必要校验；Id 用 `Guid.CreateVersion7()`；创建审计字段由框架在跟踪时填充。

```csharp
public class User : FullAuditedEntity<Guid>, IAggregateRoot<Guid>
{
    public string Username { get; private set; }
    public bool IsActive { get; private set; } = true;

    private User() { Username = null!; }

    public User(string username)
    {
        Id = Guid.CreateVersion7();
        Username = username;
    }

    public void Disable() => IsActive = false;
}
```

成组变化、带自身规则的属性收成值对象（`ValueObjects/`）：有构造不变量的继承 `ValueObject`，否则用 `record`；只读属性加 `private` 无参构造，变更返回新实例；用 `ComplexProperty` 映射，可空值对象须含必需属性，带索引的列（EF Core 11 前）保持平铺。

### 3.4 领域服务

命名 `*DomainService`，不定义接口。承载需经仓储判定的规则（如用户名唯一）与实体的创建、变更，方法按业务行为命名；读取只为判定规则或执行变更，可读外聚合、不改外聚合；不提供供展示的查询方法，不做 DTO 转换、事务管理。

规则判定在实体或领域服务；应用服务据其结果（如 `user.CanBeManagedBy(...)`）按用例选码抛出，自行组合实体字段做判定属于违规。

缓存、通知等副作用经本地事件在提交后由应用层 `IEventHandler<TEvent>` 执行：纯实体变更的由实体 `AddLocalEvent(...)` 发出；随用例而异的（如本人改密与管理员重置的提醒）由应用服务发布。

```csharp
public class UserDomainService(IRepository<User, Guid> userRepository)
{
    public async Task<User> CreateUserAsync(string username, CancellationToken cancellationToken = default)
    {
        if (await userRepository.AnyAsync(u => u.Username == username, cancellationToken))
            throw new BusinessException(UserErrorCodes.UsernameTaken, $"Username '{username}' already exists.")
                .WithData("Username", username);

        return await userRepository.InsertAsync(new User(username), cancellationToken);
    }
}
```

### 3.5 应用服务

接口 `I*AppService : IAppService`，实现 `*AppService : BaseAppService, I*AppService`。查询用仓储的 `GetQueryableAsync` 组合条件，经数据组件的 `Leistd.Data.Querying.IQueryableAsyncExecuter` 执行；DTO 投影经 `IObjectMapper`。同模块应用服务不互相调用，共用逻辑提为协作类（如 `ICaptchaVerifier`）或下沉领域层。

```csharp
public class UserAppService(
    IRepository<User, Guid> userRepository,
    IObjectMapper objectMapper,
    IQueryableAsyncExecuter asyncExecuter) : BaseAppService, IUserAppService
{
    public async Task<PagedResult<UserOutputDto>> GetPagedListAsync(
        GetUserPagedInputDto input, CancellationToken cancellationToken = default)
    {
        var query = await userRepository.GetQueryableAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(input.Keyword))
            query = query.Where(u => u.Username.Contains(input.Keyword));

        var totalCount = await asyncExecuter.CountAsync(query, cancellationToken);
        var users = await asyncExecuter.ToListAsync(
            query.OrderBy(u => u.Username).ThenBy(u => u.Id).Skip(input.Offset).Take(input.Limit),
            cancellationToken);
        return new PagedResult<UserOutputDto>(totalCount, objectMapper.Map<List<User>, List<UserOutputDto>>(users));
    }
}
```

动态排序统一使用 Dynamic LINQ；输入、验证边界和稳定次序见 [API 规范 §5](./api.md#5-分页规范)。

### 3.6 事务与工作单元

- 工作单元按需引入，不是写方法的必需装饰。单次 `SaveChanges` 已在数据库隐式事务里；只有跨多次提交边界的方法才标 `[UnitOfWork]`。判据见 `unit-of-work` 组件随包文档的"何时不需要"。
- 工作单元内仓储写入延迟到冲刷或提交：唯一约束等数据库异常在那时才抛出，不在 `InsertAsync` 调用处。
- 写方法用仓储返回值构造输出，不回查数据库：`Id`、创建审计与租户值在实体进入跟踪时已落定，而工作单元内回查得不到尚未落库的行。需要回显关联数据时让写方法回传它写了什么。

### 3.7 Controller 与组件端点

框架组件已提供端点的能力（设置、权限管理、操作记录、通知、租户与租户连接）不写 Controller：在 `backend/src/CompanyName.ProjectName.Api/Hosting/ComponentEndpoints.cs` 用组件的 `Map*` 给前缀与授权策略。组件不认识的业务动作才写 Controller，路由不与组件端点重叠。

- Controller 命名 `*Controller`，继承 `BaseController`（视图渲染、透传代理、机器端点等例外就近注释）；只做路由、鉴权与调用应用服务。
- 返回类型（含何时用 `IActionResult`）见 [API 规范 §2](./api.md#2-响应格式)，方法名与路由见 [§6](./api.md#6-http-方法与路由规范)。

操作留痕：组件端点挂 `[OperationRecordAction]` 后，授权被拒与之后的 `BusinessException` 由宿主 `ApiAuthorizationResultHandler` 和组件 `UseOperationFailureRecording()` 兜底补记（参数校验失败不记）。应用服务在拒绝处调 `RecordFailedAsync` 时兜底按动作与目标去重跳过，因此注解里的目标（含 `TargetIdPrefix`）须与应用服务记录的逐字一致；`RecordFailedAsync` 自身不判重。

### 3.8 枚举持久化

枚举以字符串持久化：`builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32)`。数据迁移中按字符串比较（`Status = 'Active'`），对 varchar 列写整数比较会在 PostgreSQL 报 42883。

### 3.9 权限

授权策略名、权限名等跨处引用的标识符用常量（`PermissionConstant`）。权限结构与界面一一对应，两端各自遵守、不互读源码：

| 权限定义 | 对应界面 | 显示名 |
| --- | --- | --- |
| 分组（`PermissionConstant.Groups`） | 菜单分组 | 与分组标题一致 |
| 根权限 `App.{模块}` | 菜单项与页面 | 与菜单项标题一致 |
| 子权限 `App.{模块}.{动作}` | 页面操作按钮 | 常规动作统一为 Create / Edit / Delete，其余与按钮文案一致 |

- `displayName` 写英文默认文案；译文键为 `Permission:{权限名}`、`PermissionGroup:{分组名}`，英文资源保留同名键且值等于默认文案（`PermissionCatalogContractTests` 核对）。
- 只作为另一个权限前提的权限不单独定义。侧别选择见 [认证与授权](./auth.md#权限侧别与租户维度)。

### 3.10 设置与 Options

| 取值层级 | 做法 |
| --- | --- |
| 租户级、用户级 | 经应用层策略提供方读 `ISettingProvider`；替别人判断用 `GetOrNullForUserAsync`。不做成 Options |
| 宿主级、运行期可改 | 在 `Api/Configuration/HostSettingBindings` 加一行绑定，消费方注入 `IOptionsMonitor<T>`/`IOptionsSnapshot<T>`；自带配置重载的库直接绑定它读的键 |
| 部署期定死（连接与签名凭据、协议参数） | `IOptions<T>`；不在服务里读 `IConfiguration["键"]` |

- 宿主级设置以最高优先级配置源覆盖部署配置，写入后本进程即时生效，其他实例由周期任务跟上；组合起来让 Options 校验不过的一组整组不生效并记错误日志。
- 绑定了配置键的宿主级设置不写代码默认值，清除即回到部署配置。
- 管理员运行期维护的第三方集成客户端凭据（发信、外部登录）走宿主级设置，口令与密钥定义为机密设置；边界以 `settings` 组件随包文档的注意事项为准（随包文档的定位见 [后端说明](../../backend/README.md#leistd-框架-api)）。
- 值域声明在设置定义上（`AsBoolean()`、`AsInteger(min, max)`、`WithAllowedValues(...)`）；定义表达不了的规则写成 `Application/Settings/Validators` 下的 `ISettingValueValidator`。跨设置的约束（如未读通知保留不短于已读）读另一项的设置链值而不是 `IOptionsMonitor`；清除也经校验器，候选值是回落后的值。
- 分组按管理员要完成的事归组，不按组件归组：新设置先归入已有分组（保留期一类都进 `Retention`），讲不进任何现有分组才新建；同一作用域的设置不散落到多个分组。允许用户覆盖的设置（`SettingScopes.All`）可以同时出现在系统与个人设置页，不为此拆定义。
- 系统设置的每个分组一条 `App.Settings.*` 子权限，登记在 `Application/Settings/Provider/SettingGroupPermissions`（设置端点的分组策略与操作记录的授权依据都从这里取）；只含进程级设置的分组子权限侧别为 `Host`。新建系统设置分组时同时加子权限定义、中英译文、前端 `PERMISSIONS.settings` 与 Mock。

## 4. 依赖注入

**注册归属**：每层的 `DependencyInjection.cs`（`AddDomainServices`、`AddApplicationServices`、`AddInfrastructureServices`/`AddPersistenceServices`、`AddApiAuthorization` 等）放项目根，注册本层类型，可调用实现本层能力所需的组件注册入口。跨宿主共用的专用注册入口也可留项目根，如 Infrastructure 的 `DataProtectionExtensions`。Api 自有类型按关注点注册在 `Api/Auth`、`Api/Hosting` 的扩展方法里（如 `AddMyProjectAuthentication`、`AddMyProjectWebHost`）；`Program.cs` 只组合各层、组件与这些入口并配置管道。部署基线 Options 在声明它的层或组合根绑定，宿主定向配置留在组合根；需要校验的用 `AddOptions<T>()...ValidateOnStart()`。

**生命周期**：先看状态所有权、并发安全、依赖链与实际消费作用域。

| 情形 | 生命周期 |
| --- | --- |
| 持有请求或工作单元内状态、依赖 DbContext | Scoped |
| 跨请求共享且线程安全（定义提供方、连接复用） | 可 Singleton |
| 其余（AppService、领域服务、事件处理器等无状态服务） | Transient |

Singleton 不得直接或间接捕获 Scoped；依赖作用域服务的 Transient 必须在正确作用域解析。生产以外的环境都开启 `ValidateScopes` 与 `ValidateOnBuild`。

**注册方式**：可替换的单实现用 `TryAdd*`；多实现用 `TryAddEnumerable`；按业务键登记（周期任务名等）与命名 Options 按各入口契约；有意覆盖组件默认实现用 `Replace` 并注释原因（`Replace` 与组件入口的调用先后无关）。相同登记重复调用不得重复生效。注册测试范围见[测试规范](./testing.md)。

没有约定式自动注册：`IAppService` 只是标记，服务需显式注册；`[UnitOfWork]` 依靠代理织入，注册时使用实现类型。

## 5. 命名与 DTO

| DTO | 命名 |
| --- | --- |
| 分页查询输入 | `Get{Entity}PagedInputDto`（继承 `PageRequest`，参数见 [API 规范 §5](./api.md#5-分页规范)） |
| 创建、更新输入 | `Create{Entity}InputDto`、`Update{Entity}InputDto` |
| 输出 | `{Entity}OutputDto` |
| Client SDK 中不成对的响应 | `{Concept}Dto` |

- DTO 全部为 record；一个文件一个对外 DTO，仅被它内嵌使用的 item 类型可同文件；业务入参 DTO 放应用层模块，不放 Api。
- 入参 DTO 写成属性式（`{ get; init; }`），不用位置记录：校验错误的 `errors[].field` 按 JSON 命名策略与请求体字段同名。
- 字段校验（必填、长度、范围）只在入口 DTO 用 DataAnnotations 完成，应用层与领域层信任 DTO 已保证的前置条件、不重复校验；例外是实体构造另有不经该 DTO 的调用路径时，其守卫是多入口共享的不变量保护，保留。参与字段校验消息的属性（带校验特性、消息里用到 `{0}`）写 `[Display(Name = "...")]`，每个校验特性显式写 `ErrorMessage`；两者写英文原文并作为本地化键，占位符形如 `{0} is required.`。前端按同一规则即时校验。
- 跨字段、依赖业务状态或 DTO 值域无法表达的规则放所属模块 `Validators/`；涉及实体不变量的规则留在领域层。验证器必须显式注册并由调用路径执行，目录名不提供自动验证。排序规则的边界见 [API 规范 §5](./api.md#5-分页规范)。
- 变量：DTO 参数 `input`，返回对象 `result`，`IQueryable` 为 `query`/`xxxQuery`；仓储注入 `{entity}Repository`，领域服务注入 `{entity}DomainService`。

### 文档注释

XML 按需补契约。短摘要写 `/// <summary>说明。</summary>`；正文需换行时，起止标签各占一行，补充信息用 `<remarks>`。[微软规范](https://learn.microsoft.com/dotnet/csharp/language-reference/language-specification/documentation-comments#d316-summary)允许两种格式，本项目优先单行。`<param>` 须完整覆盖参数，或将必要约束并入摘要后删整组，避免 CS1573。

## 6. 数据访问

- **聚合**：聚合根实现 `IAggregateRoot<Guid>`，框架只给它登记默认仓储（`EntityModelConventionTests` 核对）；子实体（如 `UserRole`）声明 DbSet 只为表名走约定，只经根的方法修改、随根持久化，修改前经根仓储显式加载。聚合间按 Id 引用，跨聚合协调在应用服务。
- **仓储**只为聚合根提供：通用 `IRepository<T, TKey>` 覆盖增删改与单个用例的查询组合（`ISoftDelete` 实体为逻辑删除）。聚合特有、被多个用例复用的查询（连接、投影）加到该聚合的自定义仓储：Domain `<模块>/Repositories/I{聚合}Repository`，Infrastructure `EfCore{聚合}Repository`，经 `AddRepository<{聚合}, EfCore{聚合}Repository>()` 登记，方法按返回内容命名（`IUserRepository.GetRoleNamesAsync`）。领域服务的读取范围见 §3.4，不新增 `*Reader`、`*Query` 等查询类型。
- **映射用默认约定**：表名、列名与第三方组件的表名保持 EF 与组件默认，不写 `ToTable(名)`、`HasColumnName`、`HasColumnType`。`HasFilter`、`HasCheckConstraint` 只写查询过滤器管不到的数据库约束（软删除或租户范围内唯一），列名用 `nameof` 拼并加双引号；换数据库时随新迁移集一并复核。
- Application 不使用 EF Core 扩展：`IQueryable` 经 `IQueryableAsyncExecuter`（`ToListAsync`、`CountAsync`、`FirstOrDefaultAsync`、`AnyAsync` 等）执行；关联数据用查询组合（子查询、`Join`）或分别查询，不用 `Include`。
- 业务库上下文经仓储或 `IDbContextProvider<TDbContext>` 获取，不直接构造注入：直接注入的实例按宿主库创建，分库租户下会落到宿主库（框架拒绝，表现为 500）。控制库上下文固定宿主连接，可以直接注入。
- 对象映射：实体、存储模型或框架模型到 DTO 的投影走模块 `Mappings/` 下实现 `IRegister` 的类，业务服务只注入 `IObjectMapper`；能按名称约定映射的不写配置；不调用无参 `Adapt<T>()`（它用全局配置，本项目的规则静默失效）；调用方才知道的值经 MapContext 传入；由多个来源拼装、带计算或本地化的 DTO 直接构造；不在 DTO 上写 `FromXxx` 静态方法。
- 请求外的异步工作交给 `IBackgroundTaskQueue`，跨实例互斥用 `IDistributedLock`，定期维护登记为周期任务（`AddRecurringJob`，显式选 `Cluster` 或 `EveryInstance`）；不另起线程或自造跨实例锁（只护进程内状态的 `lock` 除外）。
- 可还原的加密用 `IDataProtectionProvider`：构造时 `CreateProtector` 一次并复用，用途字符串带版本，解密只捕获 `CryptographicException`，可并列同一载荷的 Base64/JSON 解析异常。

## 7. 异常与日志

按失败语义选异常；HTTP 映射、错误码规则与示例见 [API 规范 §4](./api.md#4-异常与-http-映射)。

| 位置 | 异常 |
| --- | --- |
| Domain / Application 业务规则 | `BusinessException(code, safeMessage)` |
| Infrastructure 传输、配置、解析失败 | BCL 或专用技术异常（对外兜底 500，细节进日志） |
| 启动期与组合期（`Program.cs`、`Add*Services`、`IValidateOptions`）、DbMigrator 等一次性作业 | BCL 异常（`InvalidOperationException` 等） |
| 参数与编程契约 | BCL 异常 |

- 部署配置错误在启动期失败：`AddOptions<T>().Validate(...).ValidateOnStart()`。连接串在宿主启动前就要用，缺失时由创建 DbContext 直接抛出并指明键名。
- 组合期只为**选择注册哪种实现**（是否接 Redis、加载哪些证书）读配置，在读取处校验并报出键名；集成测试用 `UseSetting` 覆盖这类键。其余取值经 Options 派生，合法性用 `ValidateOnStart()` 判定。
- 日志用结构化消息模板，消息为英文：`logger.LogWarning("Login failed too many times for user {UserId}", userId)`；不可记录的内容见[通用约定 §1](./coding-common.md#1-语言与敏感信息)。

## 8. Api 目录

Api 文件按关注点归入少数顶层目录，命名空间跟随目录：

| 目录 | 内容 |
| --- | --- |
| `Controllers/` | 业务 Controller 与 `BaseController` |
| `Auth/` | 认证与授权集成，按下表分类 |
| `Hosting/` | 宿主组装扩展（`*Extensions`）、组件端点映射、`ExceptionMappings/` |
| `Localization/` | 本地化资源标记类型（`ApiResource`） |
| `Notifications/` | 只放依赖宿主资源的通知扩展点实现 |
| `Configuration/` | 宿主级设置绑定 |
| `Options/` | 强类型 Options |
| `Middlewares/` | 中间件 |
| `HealthChecks/` | `*HealthCheck` |
| `HostedServices/` 下的 `Initializer/` | 一次性启动引导 `*Initializer` |
| `Filters/` | MVC/Hub 管道过滤器（按需创建）；名字以 `Filter` 结尾的业务策略按所属功能域放 |

`Auth/` 根目录保留总体注册入口 `AuthenticationExtensions`；其余按职责归位：

| 子目录 | 内容 |
| --- | --- |
| `Authentication/` | 本地会话、远端令牌、外部登录的认证方案注册与签名密钥刷新 |
| `Authorization/` | 授权策略、授权结果处理器与访问控制元数据 |
| `Sessions/` | 会话签发与续期；通用票据存储使用 Security 组件 |
| `OpenIddict/` | 签发服务注册、证书加载、交互保护与协议处理器 |

- 周期任务（`IRecurringJob`，`*Job`）放 Application 所属模块的 `BackgroundJobs/`；常驻消费者 `*Worker` 放所属模块的 `Workers/`。不建跨模块的顶层 `Jobs/`。
- 请求体上限沿用 Kestrel 默认，大上传端点用 `[RequestSizeLimit]`/`[RequestFormLimits]` 单独放宽；不用笼统的 `Extensions` 命名空间。

## 9. Client 与 DbMigrator 目录

命名空间跟随目录。两类项目按实际职责分类，不照搬应用层目录。

| 项目 | 目录 | 内容 |
| --- | --- | --- |
| Client | 根目录 | 注册入口 `DependencyInjection`；小型 SDK 的公开客户端接口与 `*Defaults` |
| Client | `Options/` | 具体客户端的配置类型 |
| Client | `Dtos/` | SDK 自有的请求与响应契约；不引用服务内部程序集 |
| DbMigrator | 根目录 | `Program` 与注册入口 `DependencyInjection` |
| DbMigrator | `Runners/` | 命令执行类 `*Runner`，包括迁移与部署引导 |

SDK 扩展为多个业务模块时，客户端接口与 DTO 按模块归位；通用调用管道仍复用框架组件。执行器的内部结果模型可与执行器共置，不为它们另建 DTO 层。
