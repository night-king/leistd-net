# 审计

审计组件按实体标记接口自动填充创建、修改和软删除的用户与时间；业务代码无需手动赋值。

## 何时使用

| 场景 | 使用 |
| --- | --- |
| 记录创建、修改或删除信息 | 实现对应的时间、软删除或用户审计接口 |
| 同时需要全部审计字段 | 实现 `IFullAuditedObject` |
| 仅定义和读取审计字段 | 引用 `Leistd.Auditing.Core` |
| 使用 EF Core 自动填充 | 引用 `Leistd.Auditing.EntityFrameworkCore` |

`AuditSaveChangesInterceptor` 只处理修改和删除。创建审计需由 `BaseDbContext` 或 `EntityTrackingExtensions` 在实体进入跟踪时落定。

## 安装

```bash
dotnet add package Leistd.Auditing.Core
dotnet add package Leistd.Auditing.EntityFrameworkCore
```

## 注册

```csharp
builder.Services.AddAuditingEfCore();

builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    options.AddInterceptors(
        serviceProvider.GetRequiredService<AuditSaveChangesInterceptor>());
});
```

`AddAuditingEfCore` 以 Transient 注册 `IAuditPropertySetter` 和 `AuditSaveChangesInterceptor`，但不自动挂载拦截器。它要求容器已注册 `IClock`；`ICurrentUser` 可选，未注册时时间字段仍会填充，用户字段保持 `null`。

继承 `BaseDbContext` 时，应将作用域 `IServiceProvider` 传给基类构造函数；否则当前用户和运行时过滤开关不可用。普通 `DbContext` 可调用 `ChangeTracker.EnableCreationAuditing(serviceProvider)` 接入创建审计。

审计人字段的默认列长通过 EF 约定单独登记，与拦截器注册独立：

```csharp
using Leistd.Auditing.EntityFrameworkCore.Conventions;
using Microsoft.EntityFrameworkCore;

public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        => configurationBuilder.Conventions.Add(_ => new AuditingEntityConvention());
}
```

`CreatorId`、`LastModifierId`、`DeleterId` 默认最长 64；显式 Fluent 配置与数据注解优先。DDD 基座已登记此约定。

## 使用

```csharp
public class Order : IFullAuditedObject
{
    public DateTime CreationTime { get; private set; }
    public string? CreatorId { get; private set; }
    public DateTime? LastModificationTime { get; private set; }
    public string? LastModifierId { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTime? DeletionTime { get; private set; }
    public string? DeleterId { get; private set; }
}

dbContext.Add(new Order());
await dbContext.SaveChangesAsync();
```

采用 [DDD 四层基座](../ddd-struct/ddd-struct.md) 时，可直接继承 `FullAuditedEntity<TKey>`。

## 接口参考

| 类型 | 用途 |
| --- | --- |
| `IHasCreationTime` | 定义 `CreationTime` |
| `ICreationAuditedObject` | 增加 `CreatorId` |
| `IHasModificationTime` | 定义 `LastModificationTime` |
| `IModificationAuditedObject` | 增加 `LastModifierId` |
| `IHasDeletionTime` | 定义 `DeletionTime` |
| `ISoftDelete` | 定义 `IsDeleted` |
| `IDeletionAuditedObject` | 组合软删除、删除时间和 `DeleterId` |
| `IFullAuditedObject` | 组合全部审计契约 |
| `IAuditPropertySetter` | 填充创建、修改和删除审计属性 |
| `AuditSaveChangesInterceptor` | 在保存时处理修改审计和软删除 |
| `AuditingEntityConvention` | 配置审计人字段默认列长，支持普通 DbContext |
| `EntityTrackingExtensions` | 在实体进入 `Added` 状态时执行创建审计 |

## 实现行为

### 落值时机

| 状态 | 时机 | 处理者 |
| --- | --- | --- |
| `Added` | 进入变更跟踪时 | `BaseDbContext` 或 `EntityTrackingExtensions` |
| `Modified` / `Deleted` | `SavingChanges` | `AuditSaveChangesInterceptor` |

创建审计在进入跟踪时落值；`Tracked` 和 `StateChanged` 两条路径都覆盖。

### 软删除

| 写法 | EF Core 状态 | 结果 |
| --- | --- | --- |
| `Remove(entity)` | `Deleted` | 转为 `Modified`，填充删除审计 |
| 将 `IsDeleted` 设为 `true` | `Modified` | 仅在 `false -> true` 迁移时填充删除审计 |

已删除实体的后续修改不会重写删除者，也不记录修改审计。未实现 `ISoftDelete` 的实体仍按 EF Core 默认行为物理删除。

### 字段更新

- 创建和删除字段仅在尚未设置时填充。
- `LastModificationTime` 在每次修改时刷新，`LastModifierId` 同步写为当前用户；没有当前用户（后台作业、机器主体）时置空，不保留上一次的修改人。
- 时间经 `IClock.Normalize` 归一化。
- EF Core 的 `IAuditPropertySetter` 实现只接受 `EntityEntry`，其他类型抛 `ArgumentException`。

## 注意事项

- `AddAuditingEfCore` 不会自动挂载 `AuditSaveChangesInterceptor`。
- 自动填充只对实现对应审计接口的实体生效。
- 普通 `DbContext` 未接入创建审计钩子时，`Added` 实体不会自动填充创建字段。
- `ICurrentUser` 缺失时用户字段保持 `null`，不影响时间字段。

## 相关

- [当前用户与身份信息](./security.md)
- [DDD 四层基座](../ddd-struct/ddd-struct.md)
