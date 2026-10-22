# 多租户

多租户家族负责解析当前租户、校验租户状态、建立租户上下文，并为数据隔离和动态连接提供契约。

## 何时使用

| 场景 | 组合 |
| --- | --- |
| SaaS 服务按租户隔离数据 | `Core` + `AspNetCore` + `EntityFrameworkCore` |
| Resource 服务只信任已验证 token 中的租户 claim | `Core` + `AspNetCore`，关闭本地租户校验 |
| 后台任务需在指定租户下执行 | `ICurrentTenant.Change()`；带主体时用 `IAmbientContext.Begin()` 一次建立各维度（贡献者在 `AspNetCore` 包） |
| 服务间需传递租户 | 使用 ServiceClient Token Exchange；本家族从已验证令牌解析上下文 |
| 租户管理界面（查询、创建开通与补偿、启停、删除、连接登记） | `MapTenantManagement()`、`MapTenantConnections()`，宿主实现 `ITenantProvisioner` |
| 资源服务回源控制面取租户连接 | `Leistd.MultiTenancy.ServiceClient` 的 `AddRemoteTenantConnectionStore()` |
| 非多租户项目 | 不引用、不注册 |

`Core` 供 Domain/Application 引用，`AspNetCore` 供 Web 宿主引用，`EntityFrameworkCore` 供 Infrastructure 引用，`ServiceClient` 供回源控制面的资源服务引用。

## 安装

```bash
dotnet add package Leistd.MultiTenancy.Core
dotnet add package Leistd.MultiTenancy.AspNetCore
dotnet add package Leistd.MultiTenancy.EntityFrameworkCore
dotnet add package Leistd.MultiTenancy.Management    # 租户管理用例（管理界面、开通编排）
dotnet add package Leistd.MultiTenancy.ServiceClient   # 资源服务回源控制面
```

## 注册

Identity 持有租户注册表，解析匿名请求并校验租户状态：

```csharp
builder.Services.AddMultiTenancy();   // 绑定 Leistd:MultiTenancy
builder.Services.AddMultiTenancyEfCore<IdentityControlDbContext>();

var app = builder.Build();
app.UseAuthentication();
app.UseMultiTenancy();
app.UseAuthorization();
```

Resource 不复制租户注册表，只从已验证主体恢复租户：

```csharp
// 先绑定 Leistd:MultiTenancy，再应用这里的覆盖
builder.Services.AddMultiTenancy(options => options.ValidateResolvedTenant = false);
```

`ValidateResolvedTenant = false` 会停止查询 `ITenantStore` 并将解析链强制收窄为主体 claim，未经校验的请求头、查询串或域名不能选择租户。两种角色的中间件顺序相同。

`AddMultiTenancyCore()` 已登记本组件错误码的非默认 HTTP 状态（具体映射见[接口参考](#接口参考)），宿主不需要另行组合。

控制面 DbContext 映射租户表：

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    modelBuilder.ConfigureMultiTenancy();
}
```

`AddMultiTenancyEfCore<TDbContext>()` 只注册租户与连接配置的 Store/Manager，均可通过 `TryAdd` 替换；只读控制库的宿主（租户连接解析、迁移作业）到此为止。租户管理与连接管理用例在 `Leistd.MultiTenancy.Management` 包，由提供管理界面的宿主另行 `AddTenantManagement()`（需要工作单元）。它不注册业务实体的租户落值拦截器；该行为属于 DDD 基座的 `BaseDbContext`。

普通 `DbContext` 若需为 `TenantConnectionRecord` 记录创建审计，应在构造函数中显式启用创建审计，做法见[审计](./auditing.md#注册)。它只落创建审计，不落租户归属；修改审计仍由 `AuditSaveChangesInterceptor` 处理。

## 使用

### 隔离业务实体

实体通过 `IMultiTenant` 声明租户归属：

```csharp
public class Order : IMultiTenant
{
    public Guid Id { get; private set; }
    public Guid? TenantId { get; private set; }
}
```

`IMultiTenant` 本身不实施 EF Core 隔离。使用 [DDD 基座](../ddd-struct/ddd-struct.md) 时，`BaseDbContext` 会：

- 为查询添加 `TenantId == 当前租户` 过滤器，并与软删除过滤器叠加。
- 为新实体填充尚未赋值的 `TenantId`。
- 在启动时检查每个已映射的多租户实体是否存在租户过滤器。

不使用 DDD 基座时，宿主必须自行实现查询过滤和写入落值；只实现接口不会产生隔离。

宿主全局的数据（控制面表、OpenIddict 这类第三方表）放进独立的、固定宿主库的 `DbContext`，不靠关闭过滤器表达；能力边界交给权限侧别（`MultiTenancySides.Host`）。映射了 `IMultiTenant` 实体的上下文必须带租户过滤器，启动期检查会拒绝其它形态。


`TenantId` 在实体进入跟踪时落定，而非在 `SaveChanges` 时，工作单元完成前离开 `ICurrentTenant.Change()` 作用域也不会被归为宿主数据。显式赋值不会被覆盖。

### 切换租户上下文

```csharp
using Microsoft.EntityFrameworkCore;

using (currentTenant.Change(tenantId))
{
    var orders = await db.Orders.ToListAsync();
}
```

`Change()` 支持嵌套，释放时恢复父上下文。两种跨越方式语义不同：

| 方式 | 可见数据 |
| --- | --- |
| `currentTenant.Change(null)` | 仅 `TenantId == null` 的宿主数据 |
| `IDataFilter.Disable<IMultiTenant>()` | 全部租户与宿主数据 |

两者都只应用于可审计的系统能力。

### 隔离数据库之外的标识

缓存键、分布式锁键这类数据库之外的标识要自己带租户段：

```csharp
// 产出 "{租户 Id:N}:catalog:category:{id:N}"
var cacheKey = currentTenant.ScopeKey($"catalog:category:{id:N}");
```

拿到的键直接交给缓存、[分布式锁](./lock.md)或幂等存储。`ScopeKey` 产出 `{租户 Id:N}:{key}`，宿主视角为 `host:{key}`；
它是 `Leistd.MultiTenancy.Extensions` 命名空间下的 `this ICurrentTenant` 扩展。租户注册表、租户连接配置这类判断租户本身的数据不加租户段。

### 解析与校验

`AddMultiTenancy` 在解析链为空时按以下顺序装配：

| 顺序 | 来源 | 契约 |
| --- | --- | --- |
| 1 | 已认证主体 | 租户 claim（`ClaimTypeOptions.TenantId`，默认 `tenant_id`）定案；无 claim 也定案为宿主 |
| 2 | 子域名 | 仅配置 `DomainFormat` 时启用；受管域内定案 |
| 3 | 请求头 | 默认 `X-Tenant`，值可为租户 Id 或名称 |
| 4 | 查询串 | 默认 `tenant` |

主体排在首位，请求头与查询串无法改写已登录用户的租户。租户 claim 按 `ClaimTypeOptions.ReadTenant` 解析（规则见[安全组件](./security.md)）：
值必须是租户 GUID，非法一律抛 `InvalidTenantClaimException`，不回退到宿主。

三种租户通道的信任性质不同：

| 通道 | 键 | 可信性 |
| --- | --- | --- |
| 身份 claim | `ClaimTypeOptions.TenantId`（默认 `tenant_id`） | 已认证，定案 |
| 匿名请求提示 | `MultiTenancyOptions.HeaderName`（默认 `X-Tenant`）、`QueryStringParameterName`（默认 `tenant`） | 不可信，只对匿名请求生效，启用校验时须指向存在且启用的租户 |
| Token Exchange | Token Exchange JWT 的租户 claim | 身份服务确认用户所属租户，资源端从同一主体解析 |

`ICurrentTenant.Change` 在切换租户的同时打开日志作用域，键为 `TenantLogKeys.TenantId`（`leistd.tenantId`），切回宿主时值为 `null`：HTTP 解析、Hub 与后台任务的环境上下文、逐库作业等所有切换入口的日志都能按同一个键过滤。

未解析出租户表示宿主上下文。启用校验时的响应按请求是否已认证分两档：

- 未认证请求：不存在、已删除、已停用一律返回 404 `Tenant:NotFound`，状态码、错误码与响应形状一致，不暴露启用状态。
- 已认证请求：不存在返回 404 `Tenant:NotFound`，已停用返回 403 `Tenant:NotActive`。

匿名调用方仍能判断某个租户名是否存在（存在时请求走到业务逻辑，不存在时被中间件挡成 404）。这是有意接受的边界：
隐藏存在性需要为不存在的租户编造注册策略等响应；租户名本由用户在登录页输入，属于半公开信息，启用状态与租户属性匿名侧拿不到。
批量探测由部署层限流处置。

### 配置子域名

```csharp
builder.Services.AddMultiTenancy(options =>
    options.DomainFormat = "{0}.example.com");
```

`DomainFormat` 必须包含一个 `{0}`、使用纯 ASCII 主机名、不含 scheme/路径/端口，且占位符之后必须有固定基础域。错误配置在启动时抛 `OptionsValidationException`。

租户名就是主机名里的那一段，须是单个 DNS 标签（见“管理租户”）。规则只在创建与改名时校验，不合规的存量名字经子域名访问不到，要先改名。

受管域内的基础域、多级子域或不匹配前后缀的主机名定案为宿主，不再读请求头。受管域外的内部服务名继续交给后续贡献者。

反向代理使用 `X-Forwarded-Host` 时，必须通过 `KnownProxies`/`KnownIPNetworks` 限定可信代理。同时需配置授权服务器 issuer、资源服务验签、出站链接与 DNS；框架只负责入站解析。

### 管理租户

```csharp
var tenant = await tenantManager.CreateAsync(
    name,
    displayName,
    isActive: false);

using (currentTenant.Change(tenant.Id, tenant.Name))
{
    await SeedAsync(tenant);
}

await tenantManager.SetActiveAsync(tenant.Id, true);
```

`isActive` 没有默认值。需要初始化角色、管理员或业务数据时，先以停用状态创建，初始化完成后再启用。

管理界面直接用组件的用例与端点，宿主只实现开通与启用前置条件：

```csharp
builder.Services.AddTenantManagement();                                  // Leistd.MultiTenancy.Management
builder.Services.AddScoped<ITenantProvisioner, TenantSeeder>();          // 在新租户里写初始数据，失败时清掉
builder.Services.AddScoped<ITenantActivationGuard, TenantHasUsersGuard>(); // 可选，可注册多个

app.MapGroup("/api/v1/tenants").MapTenantManagement<CreateTenantWithAdminInputDto>(options =>
{
    options.ReadPolicy = "App.Tenants";
    options.CreatePolicy = "App.Tenants.Create";
    options.UpdatePolicy = "App.Tenants.Update";
    options.DeletePolicy = "App.Tenants.Delete";
});
```

`ITenantManagementService.CreateAsync` 的顺序固定：

1. 整批校验 `Connections`（名字归一化后查重、连接串语法）；
2. 以停用态登记租户，并在同一控制面工作单元里一次登记全部命名连接；
3. 在新租户上下文与新工作单元里调用 `ITenantProvisioner.ProvisionAsync`；
4. 启用。

多服务部署在创建时一次给出多条连接（`default`、`crm`……），不要建完租户再逐条补登记。`connections` 传空数组或 `null` 表示不分库；
数组里有 `null` 元素返回 400，重名返回带 `TenantConnection:NameDuplicated` 的 400。

任一步失败按 `PurgeAsync` → 逐条删连接登记 → 删租户补偿，每步独立作用域、独立令牌、各自记录失败。
数据库原因经 `ITenantDatabaseErrorDescriber` 翻成带码的 400，不回显连接串；默认实现不翻译，宿主按自己的数据库把错误映射到
`MultiTenancyErrorCodes` 的四个专属库错误码（不可达、库不存在、未迁移、凭据被拒），认不出的返回 `null` 走 5xx。

开通需要更多信息（如管理员邮箱）时派生 `CreateTenantInputDto`，端点按派生类型绑定请求体，开通器从 `TenantProvisioningContext.Input` 取回。
派生类型的必填字段写 `[Required]`（引用类型 `= null!`），不用 C# `required`：后者让缺键在反序列化阶段失败，只有 400、没有字段错误。

入参的 DataAnnotations 由用例入口执行（Minimal API 不自动验证），含创建入参里的每条连接；缺字段、超长返回带字段错误的 400，此时不写入、不发事件。
启停入参 `UpdateTenantActivationInputDto.IsActive` 是 `bool?` + `[Required]`：漏传按字段 `isActive` 拒绝，不会被当成停用。
成功的创建、更新、启停、删除发布 `TenantChangedEvent`（带显示名快照），宿主据此记审计。

删除幂等：租户不存在或已删除（含并发删除先完成）时 `DELETE /{id}` 返回 204，不发事件；并发冲突（`TenantConcurrencyConflictException`）、存储故障照常失败。
幂等只在用例层，`ITenantManager.DeleteAsync` 仍对不存在的租户抛 `TenantNotFoundException`。

`GET /` 接收 `offset`、`limit`、`keyword` 与 `isActive`（`true` / `false` 按启用状态筛选，省略不过滤，无法解析的值回 400），按创建时间倒序、同刻按 Id 倒序分页，不提供排序参数；`GetTenantPagedInputDto` 继承的 `PageRequest.Sorting` 不生效。只要数量时取结果的 `TotalCount`，它按同一组筛选计算，与分页无关。

租户名须是单个 DNS 标签：匹配 `TenantConfiguration.NamePattern`（`^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$`），最长 `TenantConfiguration.MaxNameLength`（63）个字符；
未配置 `DomainFormat` 也照样校验。`ITenantManager` 在创建与改名时校验（名称逐字不变时不校验，更新入参按存储容量 `MaxStoredNameLength`（64）接受原名），
不合法时抛 `BusinessException`（`Tenant:NameInvalid`，占位 `Name`、`Pattern`，默认 400）。大小写不限，落库的是原值。

租户名在未删除行内大小写不敏感唯一；名称冲突抛 `DuplicateTenantNameException`。租户修改与连接配置共享 `TenantRecord.Version`，并发冲突抛 `TenantConcurrencyConflictException`。

`displayName`（≤128）与 `description`（≤256）是可选的展示字段，不参与解析与唯一性判定；更新时按传入值覆盖，传 `null` 即清空。

### 配置租户连接

连接按 `(租户, 连接名)` 逐行登记，连接名就是使用方 DbContext 的 `[ConnectionStringName]`；一条都没有即该租户不单独分库，各服务使用自己配置的数据库。

是否分库在租户创建时定案：租户建出来时已有种子数据，事后补第一条连接会让解析改指一个空库。`SetAsync` 对启用中的租户拒绝改变数据落点的写入：

| 本次写入 | 在用租户 | 理由 |
| --- | --- | --- |
| 该租户一条登记都没有，登记第一条 | 拒绝 | 数据落点变了，既有数据不会跟着走 |
| 改或删已有的那一行 | 拒绝 | 新旧实例会同时写入不同物理库 |
| 已是分库租户，补一个此前没有的名字 | 放行 | 该服务此前失败关闭，没有数据搁浅 |

要把一个已建租户改成分库，走停用 → 等待排空 → 自行迁移数据 → 登记 → 重新启用。

| 登记情况 | 连接目标 | 隔离 |
| --- | --- | --- |
| 一条都没有 | 该服务自己配置的连接 | 固定 schema + `TenantId` 行隔离 |
| 登记了这个名字 | 该行的连接串 | 每个服务仍使用固定 schema + `TenantId` |
| 只登记了默认名 | 默认名那一行（一租户一库，各服务不同 schema） | 同上 |

连接串运行时与迁移共用，因此它需要 DDL 权限。名字归一化为小写后须匹配 `TenantConnectionConfiguration.NamePattern`（`^[a-z0-9-]{1,64}$`），管理员填 `Crm` 与 `crm` 命中同一行；连接串最长 `TenantConnectionConfiguration.MaxConnectionStringLength`（2048）个字符。

```csharp
// 一租户一库、各服务不同 schema：只登记默认名这一条，所有服务都回落到它
await connectionManager.SetAsync(
    tenantId,
    name: "default",
    connectionString: "Host=db-tenant-a;Database=app;Username=app;Password=...",
    expectedVersion);

// 某个服务单独用另一个库：再给它登记一条
await connectionManager.SetAsync(tenantId, "crm", "Host=db-tenant-a-crm;...", expectedVersion: null);

// 改回"用服务自己的库"就是删掉这一行，而不是提交空连接串
await connectionManager.RemoveAsync(tenantId, "crm", expectedVersion);
```

连接串加密存储：写入时用 ASP.NET Core Data Protection（`IDataProtectionProvider` + 固定 purpose）加密，读取时解密。表 `TenantConnectionRecord` 的主键是 `(TenantId, Name)`，连接列只有密文 `ProtectedConnectionString`（非空，最长 4096）。数据库、备份和只读账号里看不到明文。`TenantConnectionConfiguration` 携带明文 `ConnectionString`，它与 `TenantConnectionLookupResult`、记录实体的 `ToString` 都不输出连接串；校验与解密失败的异常消息也不回显它。

本组件不管理密钥：宿主用 `AddDataProtection()` 配置持久化、可共享的密钥环（如存 Redis）并固定应用名，读写同一控制库的所有进程（API、迁移作业）使用同一密钥环，并纳入备份。
解不开（密钥环不同或密钥已丢失）时抛 `InvalidOperationException`，不回退共享库。

`expectedVersion: null` 表示预期配置不存在；更新时必须传入读到的版本。预期与实际不一致时抛 `TenantConnectionVersionConflictException`，不提供跳过并发检查的入口。

修改已有连接（包括轮换数据库密码）前必须停用租户，否则抛 `TenantConnectionChangeRequiresInactiveTenantException`。宿主还需在变更前排空仍使用旧路由的请求。

名字与连接串通常来自管理员输入：不合法时抛带码的 `BusinessException`（`TenantConnection:NameInvalid`、`TenantConnection:ConnectionStringInvalid`），由宿主映射为 400，也不回显连接串。

管理面与机器端点：

```csharp
app.MapGroup("/api/v1/tenant-connections").MapTenantConnections(options =>
{
    options.ManagePolicy = "App.Tenants.Update";
    // 只在签发机器令牌的部署里配置：为空则不映射对应的机器端点
    options.RuntimeReadPolicy = "TenantConnection.RuntimeRead";
    options.MigrationReadPolicy = "TenantConnection.MigrationRead";
});
```

管理面（`GET /{tenantId}`、`PUT /{tenantId}/{name}`、`DELETE /{tenantId}/{name}?expectedVersion=`）只回名字与版本，不读取密文。机器端点的敏感度见「注意事项」。

`PUT` 请求体的 `expectedVersion` 必须出现、可以为 `null`：显式 `null` 表示首次登记，缺键不当成首次登记。
它保留 C# `required`（约束 JSON 里有没有这个键），缺键在反序列化阶段即被拒绝，返回不带字段错误的 400；`connectionString` 缺失按字段报错。

登记、改写、删除成功后发布 `TenantConnectionChangedEvent(TenantId, TenantDisplayName, Name, Change, Version)`，不含连接串，宿主据此留痕；
`Name` 是连接名，记录“为哪个租户改的”用 `TenantDisplayName`。写入失败时不发事件。

### 解析租户连接

最终连接由 `Leistd.Data.Connections.IConnectionStringResolver` 决定。本家族按宿主形态提供两种实现，二选一注册：

| 宿主形态 | 注册 | 宿主提供 |
| --- | --- | --- |
| 自己持有控制库（租户注册表与连接配置就在本服务） | `AddLocalTenantConnectionResolution<TControlDbContext>(o => o.ControlPlaneConnectionStringName = "IdentityControl")`（EF 包） | 持久化的 Data Protection 密钥环 |
| 连接配置在另一个服务的控制库里 | `AddRemoteTenantConnectionResolution()`（Core 包）+ `AddRemoteTenantConnectionStore(serviceName)`（ServiceClient 包） | 控制面地址 `Leistd:ServiceClients:{serviceName}:BaseAddress`、机器身份（如 client credentials）；`Leistd:MultiTenancy:Routing:CacheLifetime` 可选（默认 10 分钟）；`Leistd:ServiceClients:{serviceName}:DatabaseDirectoryCacheLifetime` 可选（默认 30 秒） |

远端存储回源控制面经 `MapTenantConnections` 映射的机器端点；路由前缀默认 `/api/v1/tenant-connections`，经 `Leistd:ServiceClients:{serviceName}:RoutePrefix` 修改。
控制面下发解密后的连接串，远端服务不持有控制面的密钥环。鉴权（如[服务客户端](./service-client.md)的 `AddClientCredentials()`）与弹性处理器加在返回的 `IHttpClientBuilder` 上：

```csharp
builder.Services.AddRemoteTenantConnectionResolution();
IHttpClientBuilder controlPlane = builder.Services.AddRemoteTenantConnectionStore("Identity");
```

只有带 `Tenant:NotFound` 错误码的 404 翻成“租户不存在”（`null`，由解析器失败关闭）；路由配错之类的其它 404 与其它错误原样抛出。

两种解析的路由判定一致：

| 情形 | 结果 |
| --- | --- |
| 连接名是控制库连接名（仅本地解析） | 宿主配置，不参与租户路由 |
| 没有当前租户 | `ConnectionStrings:{名称}`，未配置则回落 `ConnectionStrings:Default` |
| 租户一条连接都没登记 | 同上——该租户不单独分库 |
| 命中这个名字的登记 | 该行的连接串（本地解析时为解密后的值） |
| 命中默认名的登记 | 默认名那一行的连接串 |
| 登记过连接、却缺这个名字且无默认名 | 抛 `InvalidOperationException`，**不回落本服务的库** |
| 租户不存在或已删除 | 抛 `TenantNotFoundException`（带 `Tenant:NotFound` 错误码） |
| 本地解析时解密失败 | 抛 `InvalidOperationException`，不回落 |
| 连接名不合法（不匹配 `NamePattern`，来自 `[ConnectionStringName]` 的编程错误） | 抛 `ArgumentException` |

分库租户缺这个服务的连接且没有默认名可回落时失败关闭，不连到本服务的库。宿主连接的 `ConnectionStrings:Default` 回落是有意的：上下文改名后部署仍只需配置 `ConnectionStrings__Default`。

没注册租户连接解析时上表都不适用，`DbContext` 一律沿用宿主自己配置的连接；有独立库租户的系统漏装路由会让数据静默写进共享库。因此：

- 有独立库租户的系统，每个访问租户数据的宿主（Web、迁移作业、后台服务）都要注册路由（本地或远端二选一）；
- 多个宿主时，把路由注册放进它们共用的组合方法；
- 需要硬保证时，在业务 `DbContext` 里加守卫。

- **本地解析**：控制库连接名解析为 `ConnectionStrings:{名称}`，未配置时回落 `Default`；租户配置从"未删除租户"入口查询，已软删或无配置的租户抛 `TenantNotFoundException`。控制库上下文直接注入、不经 `IDbContextProvider`。
- **远端解析**：结果按 `Leistd:MultiTenancy:Routing:CacheLifetime` 缓存；同租户并发请求合并为一次回源；响应的 `TenantId` 与请求不一致时抛 `InvalidOperationException`，校验在使用连接串和写缓存之前。
- **迁移目标**：两种注册都附带 `ITenantMigrationTargetProvider`，按连接名枚举独立物理库，同一连接串只出现一次（代表租户取标识最小者）；
  一条连接都没登记的租户不出现（随宿主库迁移）。结果带明文连接串，只给迁移作业（`MigrationReadPolicy`）。
  取不出连接的租户列进 `TenantMigrationTargetSet.FailedTenants`，不挡住其他库；迁移作业须迁完其余库后报出它们并以失败结束。
  读不出清单本身（控制库查询失败、远端回源失败、取消）照常抛出。本地迁移作业同样要解密，须与 API 共享密钥环。
- **运行时逐库处理**：`AddMultiTenancyCore()` 注册 `ITenantDatabaseEnumerator`，为归档、清理这类后台作业列出物理库：宿主库在前，其后是独立库，不含连接串；
  没有注册租户连接解析时只有宿主库。停用租户的库是否参与由调用方用 `activeOnly` 显式决定。
  `activeOnly: false` 表示停用租户的数据义务仍然存在：登记仍在却解析不出（已有登记但精确名与 `Default` 都不存在，或解密失败）记入 `UnresolvedTenants`，
  库连不上或执行失败记入 `FailedDatabases`，两者都算本轮失败，框架不区分“库已撤”与“库故障”。配置遗漏或暂时不可达时恢复配置或连通性；
  宿主确认该租户的数据与保留义务已经了结、不再恢复时删除租户：软删除后它不再被列出，但不会删除业务数据或物理库。
  不要用删光连接登记来消除失败，那样该租户改走宿主库，不是退役。
  `IgnoreQueryFilters()` 只放开同一个库里的租户，独立库要逐个进入：`ICurrentTenant.Change(database.TenantId)` → `Begin(requiresNew: true)` → 经 `IDbContextProvider` 取上下文
  （直接注入的 `DbContext` 不跟随租户路由）。`ITenantDatabaseRunner.ForEachDatabaseAsync` 封装了切租户与逐库失败隔离。
  与宿主连接配置相同的登记并入宿主库；指纹判的是连接配置而非物理库，逐库逻辑须能重复执行。
  远端形态的库目录按连接名与 `activeOnly` 在进程内缓存 `DatabaseDirectoryCacheLifetime`（默认 30 秒），同键并发回源合并，回源失败不缓存，过期后重新回源。
  新增、停用或改路由的库最多晚一个周期出现在清单里，`activeOnly: true` 的作业可能晚一个周期才发现变化；`FailedTenants` 随清单一起缓存，同一周期内保持原样。
  进库用的连接仍走路由缓存（`Routing:CacheLifetime`），缩短本期限不会让路由变更更快生效。本地形态直接读控制库，不缓存。

```json
{
  "Leistd": {
    "MultiTenancy": {
      "Routing": { "CacheLifetime": "00:05:00" }
    }
  }
}
```

`CacheLifetime` 默认 10 分钟，同时决定改租户路由前的排空等待：`max(Access Token 有效期, CacheLifetime)`。

固定在控制库的 DbContext 声明专属连接名，与 `ControlPlaneConnectionStringName` 一致：

```csharp
using Microsoft.EntityFrameworkCore;

[ConnectionStringName("IdentityControl")]
public sealed class IdentityControlDbContext : DbContext;
```

## 接口参考

| 类型 | 用途 |
| --- | --- |
| `Leistd.MultiTenancy.Tenancy.IMultiTenant` | 通过 `Guid? TenantId` 声明数据归属；`null` 表示宿主 |
| `Leistd.MultiTenancy.Context.ICurrentTenant` | 读取或临时切换租户上下文 |
| `CurrentTenantKeyExtensions.ScopeKey(currentTenant, key)` | 把缓存键、锁键等租户外部标识限定到当前租户；`this ICurrentTenant` 扩展 |
| `Leistd.MultiTenancy.Stores.ITenantStore` | 按 Id 或归一化名称查找租户 |
| `Leistd.MultiTenancy.Stores.ITenantManager` | 创建、修改、启停、软删除和分页查询租户；`GetPagedAsync(keyword, isActive, PageRequest, ct)` 只查未删除的租户，`isActive` 为 `null` 不按状态过滤，返回 `PagedResult<TenantConfiguration>` |
| `ITenantManagementService` | 租户管理用例：`GetPagedAsync`、`GetAsync`、`CreateAsync`（登记→开通→启用与补偿）、`UpdateAsync`、`SetActivationAsync`、`DeleteAsync`；Management 包注册。不提供按名字查询 |
| `ITenantProvisioner` | 宿主实现：`ProvisionAsync(context, ct)` 在新租户里写初始数据，`PurgeAsync(context, ct)` 幂等清除；未注册时不开通 |
| `ITenantActivationGuard` | 宿主实现：手动启用前的前置条件，不满足时抛带码异常 |
| `ITenantDatabaseErrorDescriber` | 把开通时的数据库错误翻成带码的 400；默认不翻译，宿主按自己的数据库实现并在组件注册前登记 |
| `TenantChangedEvent` | 管理用例成功后发布：`TenantId`、`DisplayName`、`Change`（`Created`/`Updated`/`ActivationChanged`/`Deleted`） |
| `ITenantConnectionManagementService` | 连接管理用例：`GetListAsync`、`GetRuntimeAsync`、`GetMigrationListAsync`、`GetDatabaseListAsync`、`SetAsync`、`RemoveAsync`；Management 包注册 |
| `ITenantConnectionDirectory` | 列出租户已登记的连接名与版本；租户不存在返回 `null`，不分库返回空列表 |
| `MultiTenancyErrorCodes` | 组件错误码，默认中英译文随包分发 |
| `TenantConfiguration.NamePattern` / `MaxNameLength` | 租户名规则（单个 DNS 标签，≤63）；前端表单可直接复用这个模式 |
| 默认 HTTP 状态 | 组件默认状态：租户停用（`Tenant:NotActive`）→ 403，不存在（`Tenant:NotFound`）→ 404；租户版本冲突（`Tenant:ConcurrencyConflict`）、重名（`Tenant:DuplicateName`）、连接版本冲突（`TenantConnection:VersionConflict`）与租户未停用时修改连接（`TenantConnection:ChangeRequiresInactiveTenant`）→ 409。其余（如 `Tenant:NameInvalid`、`TenantConnection:NameInvalid`）按业务异常默认 400。由 `AddMultiTenancyCore()` 自动登记；宿主 `MapCode` 可覆盖 |
| `MapTenantManagement<TCreateInput>(configure)` / `MapTenantConnections(configure)` | AspNetCore 包：租户管理与连接端点；策略名必填，端点名前缀 `TenantManagementEndpoints.NamePrefix`；只有 `by-host` 匿名，它跑宿主配置的解析链，与真实请求给出同一个答案 |
| `UseTenantSessionRecovery(configure?)` | AspNetCore 包：租户会话自恢复中间件；按 `ClaimTypeOptions.ReadTenant` 判定租户会话（主体属于某个租户；声明非法时保留原始错误）；`SignOutScheme`、`TenantInvalidHeader`（默认 `X-Tenant-Invalid`） |
| `AddRemoteTenantConnectionStore(serviceName, configure?, configSectionPath?)` | ServiceClient 包：远端连接存储，返回 `IHttpClientBuilder`；与控制库的 EF 存储二选一，相同参数重复调用幂等，换用另一服务名或配置节时抛出；默认绑定 `Leistd:ServiceClients:{serviceName}`，`BaseAddress` 缺失或不是绝对地址时启动失败并报出实际键名；该客户端不转发用户与租户上下文（控制面查询，租户 Id 在路径里） |
| `ITenantConnectionConfigurationManager` | `SetAsync(tenantId, name, connectionString, expectedVersion, ct)` 登记或更新一条；`RemoveAsync(tenantId, name, expectedVersion, ct)` 删除一条（该名字随即回落到服务自己的配置）。会改变数据落点的写入要求租户已停用，判据见上文表格 |
| `AddTenantManagement()` | Management 包：注册 `ITenantManagementService`、`ITenantConnectionManagementService` 与开通错误翻译的默认实现（不翻译），并调用 `AddMultiTenancyCore()`；存储契约与工作单元由调用方先就位 |
| `AddLocalTenantConnectionResolution<TControlDbContext>(configure)` | EF 包：注册本地连接解析与本地迁移目标，并调用 `AddMultiTenancyCore()`；`LocalTenantConnectionOptions.ControlPlaneConnectionStringName` 必填且不能是 `Default` |
| `AddRemoteTenantConnectionResolution(configSectionPath?)` | Core 包：注册远端连接解析、`HybridCache`（只用进程内一级，并发回源合并为一次，连接串不进分布式缓存）与远端迁移目标，并调用 `AddMultiTenancyCore()`；绑定 `configSectionPath`（默认 `Leistd:MultiTenancy:Routing`），校验消息按实际路径报键，重复调用换用另一配置节时抛出 |
| `ITenantConnectionConfigurationStore` | 按名字读连接：`FindAsync(tenantId, name, ct)` 返回 `TenantConnectionLookupResult`（租户不存在或已删除时为 `null`），“精确名 → 默认名”的回落由实现完成；`GetListAsync(name, ct)` 供迁移作业枚举，返回 `TenantMigrationConnectionListResult(Connections, FailedTenants)`。EF 包提供控制库实现，ServiceClient 包提供远端实现，一次只按名字返回一条 |
| `TenantConnectionLookupResult` | `HasAnyConnection` 区分"不分库"与"缺这个名字"；`Connection` 是命中的那一条 |
| `TenantRouteCacheOptions` | `CacheLifetime`（默认 10 分钟，不超过 `MaximumCacheLifetime` 1 小时） |
| `RemoteTenantConnectionClientOptions` | ServiceClient 包：`RoutePrefix`（默认 `/api/v1/tenant-connections`）；`DatabaseDirectoryCacheLifetime`（远端库目录缓存，默认 30 秒，大于 0、不超过 1 小时，越界启动失败并报键名） |
| `ITenantMigrationTargetProvider` | `GetDedicatedTargetsAsync(name, ct)` 返回 `TenantMigrationTargetSet(Targets, FailedTenants)`：按连接名枚举的独立物理库 `TenantMigrationTarget(TenantId, ConnectionString)`，每个库一条，`Fingerprint` 为连接串的 SHA-256；取不出连接的租户为 `TenantDatabaseFailure(TenantId, Reason)`，`Reason` 不含连接串 |
| `ITenantDatabaseDirectory` | 控制库侧的库目录：按连接名回 `TenantDatabaseListResult(Databases, FailedTenants)`，不含连接串。EF 实现随 `AddMultiTenancyEfCore` 注册（它读的就是控制库），ServiceClient 包以远端存储实现它并加短时缓存；自定义存储的宿主要自己注册，`AddTenantManagement` 少了它解析不出连接管理用例 |
| `ITenantDatabaseEnumerator` | `GetDatabasesAsync(name, activeOnly, ct)` 列出运行时要逐库处理的物理库 `TenantDatabase(TenantId, Fingerprint, TenantIds)`；`TenantId` 为 `null` 即宿主库，宿主项的 `TenantIds` 为空。与宿主同配置的登记并入宿主库 |
| `ITenantDatabaseRunner` | `ForEachDatabaseAsync(name, activeOnly, action, ct)` 在每个物理库的代表租户上下文里执行回调，逐库隔离失败，返回 `TenantDatabaseRunResult(Databases, FailedDatabases, UnresolvedTenants)`；不替回调开工作单元。判断本轮是否成功时 `FailedDatabases` 与 `UnresolvedTenants` 都要看 |
| `Leistd.MultiTenancy.Tenancy.MultiTenancySides` | 表示权限属于 `Tenant`、`Host` 或 `Both` |

`ITenantStore` 只有 EF 一种实现，由 `AddMultiTenancyEfCore<TDbContext>()` 注册。资源服务不注册它：把 `ValidateResolvedTenant` 置为 `false` 后解析链只信已验证主体的租户声明，中间件不查注册表。

宿主若必须绕过 Store 直接查询控制面表，必须从以下扩展起查：

| 扩展 | 结果 |
| --- | --- |
| `DbContext.UndeletedTenants()` | 未删除的 `TenantRecord` |
| `DbContext.ConnectionsOfUndeletedTenants()` | 未删除租户的连接配置 |

控制面上下文通常不带软删除或租户过滤器，直接 `Set<T>()` 可能让已删除租户继续解析连接。

## 配置项（`Leistd:MultiTenancy`）

| 属性 | 默认值 | 说明 |
| --- | --- | --- |
| `HeaderName` | `X-Tenant`（`DefaultHeaderName`） | 匿名请求的租户提示头名 |
| `QueryStringParameterName` | `tenant` | 匿名请求的租户提示查询参数名 |
| `ValidateResolvedTenant` | `true` | 是否查租户注册表校验存在且启用 |
| `DomainFormat` | `null` | 子域名解析格式，如 `{0}.example.com` |

## 注意事项

- 中间件顺序必须是 `UseAuthentication()` →（`UseTenantSessionRecovery()`）→ `UseMultiTenancy()` → `UseAuthorization()`。不挂会话自恢复时，被停用租户的用户连登录页与注销端点都访问不了；跨域部署要把恢复标记头加进 CORS 暴露头。
- 连接的机器端点只对机器主体开放，敏感度不同：`GET /migration?name=` 下发全部租户的明文连接串，只给一次性迁移作业的 DDL 身份（`MigrationReadPolicy`），不要给常驻 API；
  `GET /runtime/{tenantId}?name=` 下发被问到的那一条；`GET /databases?name=` 不下发连接串，与 `/runtime` 同属 `RuntimeReadPolicy`，供常驻服务的逐库作业使用。
  不签发机器令牌的部署不要配置 `RuntimeReadPolicy` / `MigrationReadPolicy`。
- 认证端必须向租户用户签发单一、有效的租户 claim（类型见 `ClaimTypeOptions.TenantId`，签发与读取两端同一配置）；漏写会将 Resource 请求当作宿主上下文。
- `IgnoreQueryFilters()` 与 raw SQL 会绕过租户隔离。合法的跨租户操作使用 `IDataFilter.Disable<IMultiTenant>()` 显式表达。
- 租户实体的唯一索引需分别覆盖 `TenantId IS NULL` 的宿主行和 `TenantId IS NOT NULL` 的租户行；单一 `(TenantId, X)` 索引无法限制多个 NULL。
- 承载租户业务数据的外部标识（缓存 key、锁 key 等）必须用 `ScopeKey` 带租户作用域；控制面标识（租户注册表、租户连接配置）保持全局。
- 超级管理员只可旁路功能权限，不可旁路租户数据隔离。
- 多服务系统的租户注册表应归 Identity 所有，Resource 在令牌有效期内信任经验证的 claim。租户停用或删除在 Identity 立即生效，在 Resource 要等已签发的 Access Token 过期。
  要即时生效，需在停用时撤销该租户已签发的令牌并让 Resource 做令牌内省，框架不内置；只开内省不够，内省不查租户状态。
- `EfCoreTenantStore` 默认不缓存，使启停和删除在提交后立即生效。自行加缓存时必须显式承担撤销延迟。

## 相关

- [DDD 基座](../ddd-struct/ddd-struct.md)：查询过滤、写入落值与启动检查
- [审计](./auditing.md)：创建与修改审计
- [授权](./authorization.md)：权限的多租户侧别
- [服务调用](./service-client.md)：跨服务租户传递
