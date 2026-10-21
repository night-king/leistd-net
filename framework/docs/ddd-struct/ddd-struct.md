# DDD 四层基座

DDD 基座为 Domain、Application.Contracts、Application 和 Infrastructure 提供实体、仓储、数据过滤、DTO 与 EF Core 集成。

## 何时使用

| 层 | 包 | 主要能力 |
| --- | --- | --- |
| Domain | `Leistd.Ddd.Domain` | 实体、审计基类、仓储契约、数据过滤器 |
| Application.Contracts | `Leistd.Ddd.Application.Contracts` | 应用服务契约、DTO、分页类型 |
| Application | `Leistd.Ddd.Application` | 应用服务标记基类、分页映射 |
| Infrastructure | `Leistd.Ddd.Infrastructure` | EF Core 仓储、DbContext 基类、过滤器和本地事件 |

业务项目通常每层建立一个工程并只引用对应包，层间依赖为 Application → Application.Contracts → Domain。

框架包本身：`Leistd.Ddd.Application` 只引用 `Application.Contracts` 与 `Leistd.ObjectMapping.Core`（分页映射需要 `IObjectMapper`）；
`Application.Contracts` 不依赖映射组件；`Leistd.Ddd.Infrastructure` 依赖 `Domain`。

### DbContext 显式接入

每个注册过的 DbContext 都要调用一次 `AddDddDbContext<TDbContext>()`，完整写法见[注册](#注册)。

漏掉的上下文不受租户过滤器闸门检查。装了 `DynamicProxyServiceRegistrationCallbackFactory` 时，漏写在构建容器时失败；
不装则框架查不出漏登记。不需要仓储的上下文调用无参重载即可。框架不扫描容器自动发现上下文。

默认仓储只给聚合根：实体来源是公开的 `DbSet<T>` 声明中实现 `IAggregateRoot` 的类型。子实体可以声明 `DbSet`
让表名走命名约定，不会得到仓储，只能经根修改。只经 `modelBuilder` 映射的聚合根用 `AddDefaultRepository<TEntity>()`
点名，或用 `AddRepository<TEntity, TImpl>()` 指定自定义实现。

同一实体被两个上下文各注册一次时抛异常；同一上下文以相同选项重复登记不会抛，也不会多出注册。

### 应用服务基类

`BaseAppService`（`Leistd.Ddd.Application.AppServices`）当前只实现
`IAppService`（`Leistd.Ddd.Application.Contracts.AppServices`）标记，不参与注册或织入；服务仍需显式注册，
横切行为由特性和拦截器提供。派生服务的依赖应保留在构造函数中，不通过 `IServiceProvider` 隐藏。

## 安装

```bash
dotnet add package Leistd.Ddd.Domain
dotnet add package Leistd.Ddd.Application.Contracts
dotnet add package Leistd.Ddd.Application
dotnet add package Leistd.Ddd.Infrastructure
```

## 注册

注册基础设施、每个 DbContext 和所需拦截器：

```csharp
// 拦截器织入与漏登记校验依赖此工厂。
builder.Host.UseServiceProviderFactory(new DynamicProxyServiceRegistrationCallbackFactory());

builder.Services.AddDddInfrastructure(options =>
{
    options.IsTransactional = true;
});

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

// 每个 DbContext 显式登记一次；不需要仓储时用无参重载。
builder.Services.AddDddDbContext<AppDbContext>(o => o.AddDefaultRepositories());
builder.Services.AddDddDbContext<ControlPlaneDbContext>();
```

`AddDddInfrastructure()` 注册工作单元、本地事件总线、EF Core 支持和数据过滤器，工作单元选项绑定 `Leistd:UnitOfWork` 配置节，传入的委托在绑定之后应用；仓储由 `AddDddDbContext<TDbContext>()` 按上下文显式注册。登记派生自 `BaseDbContext` 的上下文时，它经官方 `ConfigureDbContext<TDbContext>` 挂载三个保存拦截器（与 `AddDbContext` 的先后无关，重复登记不会挂第二层）：

- `AuditSaveChangesInterceptor`：修改/删除审计与软删除转换。
- `LocalEventSaveChangesInterceptor`：收集并发布实体本地事件。
- `ConcurrencyStampSaveChangesInterceptor`：配置并换发乐观并发标记。

不继承 `BaseDbContext` 的上下文不挂载。控制面上下文若只需审计，在 `AddDbContext` 中单独添加 `AuditSaveChangesInterceptor`。拦截器按作用域解析，因此不支持 DbContext 池与默认（单例）生命周期的 `AddDbContextFactory`：其选项为单例，解析拦截器与 `BaseDbContext` 所用的都是根容器。

## 使用

### 定义实体与本地事件

```csharp
public class Order : FullAuditedEntity<Guid>, IAggregateRoot<Guid>
{
    public string CustomerName { get; private set; } = string.Empty;

    private Order() { }

    public Order(Guid id, string customerName) : base(id)
    {
        CustomerName = customerName;
        AddLocalEvent(new OrderCreatedEvent(id));
    }
}
```

审计字段由基础设施填充，业务代码不手动赋值。创建审计在实体进入跟踪时落定，修改与删除审计在保存时落定；详见[审计组件](../components/auditing.md)。

### 通过仓储读写

```csharp
public class OrderManager(IRepository<Order, Guid> repository)
{
    public Task<Order?> GetAsync(Guid id, CancellationToken ct = default)
        => repository.GetByIdAsync(id, ct);

    public Task<Order> CreateAsync(Order order, CancellationToken ct = default)
        => repository.InsertAsync(order, ct);
}
```

仓储写入在工作单元内延迟到统一提交，在工作单元外立即调用 `SaveChangesAsync`。`GetByIdAsync` 使用过滤查询而非 `FindAsync`，不会绕过软删除或租户隔离。

唯一索引等约束冲突在冲刷时才抛出，不在 `InsertAsync` 抛出；工作单元内要就地处理并发首次写入，先
`IUnitOfWork.SaveChangesAsync`（见[工作单元](../components/unit-of-work.md#在事务内提前冲刷)）：

```csharp
// 错：catch 永不触发
try { await repository.InsertAsync(entity, ct); }
catch (DbUpdateException) { /* 死代码 */ }

// 对：先冲刷，冲刷才是抛出点
await repository.InsertAsync(entity, ct);
try { await unitOfWorkManager.Current!.SaveChangesAsync(ct); }
catch (DbUpdateException) { /* 这里才捕获得到 */ }
```

### 自定义仓储接口

同一聚合的专属查询被多个用例复用时，在 Domain 声明派生自 `IRepository<TEntity, TKey>` 的接口，在 Infrastructure 继承 `EfCoreRepository<TDbContext, TEntity, TKey>` 实现，再用 `AddRepository<TEntity, TImpl>()` 登记。只被一个用例使用的查询直接经 `GetQueryableAsync` 写在用例里，不必为它新增接口。

```csharp
// Domain
public interface IOrderRepository : IRepository<Order, Guid>
{
    Task<List<Order>> GetByCustomerAsync(string customerName, CancellationToken ct = default);
}

// Infrastructure
public class OrderRepository(IDbContextProvider<AppDbContext> dbContextProvider, IUnitOfWorkManager uow)
    : EfCoreRepository<AppDbContext, Order, Guid>(dbContextProvider, uow), IOrderRepository
{
    public async Task<List<Order>> GetByCustomerAsync(string customerName, CancellationToken ct = default)
    {
        var orders = await GetDbSetAsync(ct);
        return await orders.Where(x => x.CustomerName == customerName).ToListAsync(ct);
    }
}

// 注册：IOrderRepository、IRepository<Order>、IRepository<Order, Guid> 都解析到 OrderRepository
builder.Services.AddDddDbContext<AppDbContext>(o => o
    .AddDefaultRepositories()
    .AddRepository<Order, OrderRepository>());
```

实现类上派生自 `IRepository<TEntity>` 的自定义接口与默认接口一起按 Scoped 注册。相同登记重复调用不重复生效；同一接口登记另一个实现时抛 `InvalidOperationException`，先登记的实现保持不变。

### 分页映射

```csharp
var source = new PagedResult<Order>(total, orders);
return mapper.MapPagedResult<Order, OrderDto>(source);
```

`PageRequest` 默认 `Offset = 0`、`Limit = 10`，上限为 1000。`Sorting` 的可用字段应由具体应用服务校验。

### 临时关闭数据过滤器

```csharp
using (dataFilter.Disable<ISoftDelete>())
{
    return await repository.GetListAsync();
}
```

`IDataFilter` 基于嵌套作用域恢复先前状态。多租户过滤可使用 `Disable<IMultiTenant>()` 独立关闭。

### 定义 DbContext

```csharp
public class AppDbContext(
    DbContextOptions<AppDbContext> options,
    IServiceProvider serviceProvider)
    : BaseDbContext(options, serviceProvider)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.ConfigurePermissionAuthorization();
    }
}
```

`BaseDbContext.OnModelCreating` 与 `ConfigureConventions` 已封闭；派生类覆盖 `ConfigureModel` 配置实体、覆盖 `ConfigureModelConventions` 追加模型约定（如枚举统一存为字符串）。基类在派生配置完成后为已进入模型的非 owned 根实体（含未声明 `DbSet` 的组件实体）添加命名过滤器，使用数据 EF 组件的建模原语；owned 与派生类型沿所属查询根的过滤规则加载。

基类分别登记 `AuditingEntityConvention`（审计人字段最长 64）与 `DddEntityConvention`（[乐观并发标记](#乐观并发标记)）。两者采用约定来源，Fluent 配置与数据注解优先。普通上下文只需审计时登记审计约定；另需 DDD 并发标记时再登记 DDD 约定，详见[审计组件](../components/auditing.md)。

需要审计、租户上下文或运行时过滤开关时，DbContext 必须接收 `IServiceProvider` 并传给基类。否则它固定为宿主视角，创建审计与 `IDataFilter` 作用域均无法生效。

软删除与租户过滤器分别命名为 `SoftDelete` 和 `MultiTenant`，同时命中时以 AND 叠加。注册了 `ICurrentTenant` 后，启动检查会拒绝任何未带 `MultiTenant` 过滤器的非 owned 根 `IMultiTenant` 实体。

## 接口参考

| 包 | 关键类型 |
| --- | --- |
| `Leistd.Ddd.Domain` | `Entity<TKey>`、审计实体基类、`IRepository<TEntity, TKey>` |
| `Leistd.Ddd.Application.Contracts` | `AppServices.IAppService`、`EntityDto<TKey>`、`PageRequest`、`PagedResult<T>` |
| `Leistd.Ddd.Application` | `AppServices.BaseAppService`、`MapPagedResult<TSource, TDestination>` |
| `Leistd.Ddd.Infrastructure` | `AddDddInfrastructure`、`AddDddDbContext`、`BaseDbContext`、`DddEntityConvention`、`EfCoreRepository` |

`IRepository<TEntity>` 提供查询、计数、存在性与批量写入；带主键的接口另提供按 Id 读取和删除。数据过滤端口与异步查询端口由[数据组件](../components/data.md)提供：`Leistd.Data.Filters.IDataFilter`、`Leistd.Data.Querying.IQueryableAsyncExecuter`。DDD 基础设施组合其注册与可选 EF 执行器。

EF 仓储的所有读方法均经 `GetQueryableAsync`：覆写可追加 Where、Include 或拆分查询，必须保留原实体身份和跟踪，不投影或使用 AsNoTracking。Count/Any 使用相同筛选，由 provider 执行标量而不物化关联。普通读取不自动加载业务关联，具体仓储按需要显式追加。

写入使用原始 DbSet。按 Id 删除先经读入口查找，受自定义读筛选影响；按条件或主键集合批量删除使用原始集合，仅受全局过滤器影响。

审计实体层次为 `CreationAuditedEntity<TKey>` → `ModificationAuditedEntity<TKey>` → `DeletionAuditedEntity<TKey>`；`FullAuditedEntity<TKey>` 聚合创建、修改与删除审计契约。

## 实现行为

- `AddDddDbContext<TDbContext>(o => o.AddDefaultRepositories())` 扫描该上下文的 public `DbSet<>` 属性，为其中的聚合根注册 Scoped 仓储；不传选项即只登记上下文、不注册仓储。未声明 `DbSet<>` 的聚合根用 `AddDefaultRepository<TEntity>()` 点名。
- `LocalEventSaveChangesInterceptor` 在保存前收集并清空实体事件，保存成功后加入当前工作单元；无工作单元时直接发布。保存失败会丢弃本次收集。
- 使用同步 `SaveChanges()` 时，本地事件发布需要 sync-over-async 并记录 Warning；应优先使用 `SaveChangesAsync()`。
- 全局过滤器只应用于 EF Core 模型中的非 owned 根实体类型，派生实体不重复添加，owned 不设置独立过滤器。

## 建模原语

### 聚合根

```csharp
public class Order : FullAuditedEntity<Guid>, IAggregateRoot<Guid>;
```

`IAggregateRoot<TKey>` 可与实体或审计基类组合；该标记本身不限制仓储泛型。

### 值对象

```csharp
public sealed class Money : ValueObject
{
    public decimal Amount { get; }
    public string Currency { get; }

    public Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency.ToUpperInvariant();
    }

    protected override IEnumerable<object?> GetAtomicValues()
    {
        yield return Amount;
        yield return Currency;
    }
}
```

`ValueObject` 按严格运行时类型与 `GetAtomicValues()` 结果实现相等、哈希和运算符。所有字段都参与相等且无构造不变量时，直接使用 `record`；需要排除派生字段或防止 `with` 绕过构造校验时使用本基类。

### 乐观并发标记

```csharp
public class Document : Entity<Guid>, IHasConcurrencyStamp
{
    public string ConcurrencyStamp { get; set; } = ConcurrencyStamps.New();
}
```

`DddEntityConvention` 将该属性配置为必填、最长 40 的并发令牌。`ConcurrencyStampSaveChangesInterceptor` 在新增时补种空值，在修改时换发；并发更新的落败方收到 `DbUpdateConcurrencyException`。

断开连接更新时，应将客户端回传的标记设置为 EF Core `OriginalValue`。

## 迁移快照检查

模型约定和组件实体配置随框架版本变化。更新框架后核对迁移快照与当前模型一致；不一致时真实库上执行迁移会失败，InMemory 测试察觉不到。

在测试中经设计时工厂检查，不连库、不装 `dotnet-ef`：

```csharp
[Fact]
public void The_migration_snapshot_matches_the_model()
{
    using var dbContext = new AppDbContextFactory().CreateDbContext([]);
    Assert.False(dbContext.Database.HasPendingModelChanges());
}
```

或在命令行执行 `dotnet ef migrations has-pending-model-changes --context AppDbContext`。有差异时用 `dotnet ef migrations add` 生成迁移。

## 注意事项

- 业务 DbContext 必须继承 `BaseDbContext` 并经 `AddDddDbContext<TDbContext>()` 登记；否则修改/删除审计、软删除转换、本地事件与并发标记都不生效。
- 实现 `ISoftDelete` 的实体在删除时转为逻辑删除，查询默认不可见；临时读取使用 `IDataFilter.Disable<ISoftDelete>()`。
- `AddLocalEvent` 只由实体内部调用；`GetLocalEvents()` 与 `ClearLocalEvents()` 仅供基础设施使用。
- `PagedResult<T>` 将 `null` items 视为空集合；`MapPagedResult` 对空 mapper 或 source 抛 `ArgumentNullException`。
- Entity 默认使用引用相等；需要按主键值相等时由业务类型明确实现。

## 相关

- [审计](../components/auditing.md)
- [多租户](../components/multi-tenancy.md)
- [工作单元](../components/unit-of-work.md)
- [事件总线](../components/event-bus.md)
