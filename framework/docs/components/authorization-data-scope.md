# 数据范围

数据范围决定列表、统计、导出能看到哪些候选数据，在查询构造阶段翻译成 SQL 谓词而不是先加载再过滤。框架只定义策略与组合语义，「本人」「本部门」等业务概念由业务项目注册 Provider 实现。

## 何时使用

| 场景 | 推荐 |
| --- | --- |
| 有后台列表、分页总数、聚合统计、导出或批量审批 | 引入本组件，所有集合入口统一走 `IDataScopeApplier` |
| 可见范围是"本人/本组织/下级组织/负责区域"等集合关系 | 引入本组件；用 ACL 物化这类关系会产生大量同步写与孤儿记录 |
| 数据量大，不可能加载候选后逐条检查 | 引入本组件 |
| 只按精确 ID 打开单个资源，几乎没有列表 | 不需要本组件，见[资源实例授权](./authorization-resource.md) |
| 只需要判断"能否执行这类动作" | 不需要本组件，见[功能权限](./authorization.md) |

## 安装

```bash
dotnet add package Leistd.Authorization.DataScope.Core
```

本包依赖 `Leistd.Authorization.Core` 和 Microsoft DI 抽象，不依赖 EF Core 或 DDD 基座；它只接收 `IQueryable<TEntity>` 与表达式。

## 注册

```csharp
builder.Services.AddDataScopeCore();

// 业务实现：当前主体在某类资源的某个操作上被分配了哪些范围
builder.Services.AddScoped<IDataScopeAssignmentProvider, OrganizationScopeAssignmentProvider>();

// 注册本文的“本人”范围；同一实体可按需添加其他 Provider
builder.Services.AddDataScopeProvider<Order, OwnOrderScopeProvider>();
```

依赖调用方已注册 `IPermissionSubjectProvider`；`IDataScopeAssignmentProvider` 由业务项目提供（角色配置表、组织架构或外部策略服务等）。

## 使用

实现范围 Provider，返回谓词以便多个范围取并集：

```csharp
using System.Linq.Expressions;

public class OwnOrderScopeProvider : IDataScopeProvider<Order>
{
    public const string Scope = "Own";

    public string ResourceName => "Orders";
    public string ScopeName => Scope;

    public ValueTask<Expression<Func<Order, bool>>> BuildPredicateAsync(
        DataScopeContext context,
        CancellationToken cancellationToken = default)
    {
        var userId = context.Subject.UserId;
        return ValueTask.FromResult<Expression<Func<Order, bool>>>(
            order => order.OwnerId == userId);
    }
}
```

分配来源按操作返回范围；"能看"不等于"能改"：

```csharp
using Microsoft.EntityFrameworkCore;

public class OrganizationScopeAssignmentProvider(ScopeDbContext dbContext)
    : IDataScopeAssignmentProvider
{
    public async ValueTask<IReadOnlyList<DataScopeAssignment>> GetAssignmentsAsync(
        PermissionSubject subject,
        string resourceName,
        string operation,
        CancellationToken cancellationToken = default)
    {
        var roleIds = subject.RoleIds.Select(Guid.Parse).ToList();

        var rows = await dbContext.Set<RoleDataScope>()
            .AsNoTracking()
            .Where(x => roleIds.Contains(x.RoleId)
                        && x.ResourceName == resourceName
                        && x.Operation == operation)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(x => new DataScopeAssignment(
            x.ResourceName, x.Operation, x.ScopeName, x.ScopeValue))];
    }
}
```

列表、总数和导出必须复用施加范围后的同一查询：

```csharp
using Microsoft.EntityFrameworkCore;

var scoped = await dataScope.ApplyAsync(dbContext.Set<Order>(), "Orders", DataOperations.Read, ct);

if (!string.IsNullOrWhiteSpace(keyword))
{
    scoped = scoped.Where(order => order.Code.Contains(keyword));
}

var totalCount = await scoped.LongCountAsync(ct);
var items = await scoped.OrderBy(order => order.Code).Skip(offset).Take(limit).ToListAsync(ct);
```

批量操作应先在范围内定位目标并核对数量，不能静默跳过越权项：

```csharp
using Microsoft.EntityFrameworkCore;

var scoped = await dataScope.ApplyAsync(dbContext.Set<Order>(), "Orders", DataOperations.Update, ct);

var targets = await scoped.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
if (targets.Count != ids.Count)
{
    throw new UnauthorizedAccessException("Some of the selected orders are out of your data scope.");
}

foreach (var order in targets)
{
    order.Approve();
}
```

DDD 仓储与分页组合见 [DDD 四层基座](../ddd-struct/ddd-struct.md)。

## 接口参考

`Leistd.Authorization.DataScope` 命名空间：

| 成员 | 说明 |
| --- | --- |
| `DataOperations` | 内置操作名常量：`Read`、`Update`、`Delete`、`Export`；业务可自行扩展 |
| `DataScopeAssignment` | 范围分配：`ResourceName`、`Operation`、`ScopeName`、`ScopeValue` |
| `DataScopeContext` | 解析上下文：`Subject`、`ResourceName`、`Operation`、`Assignments` |
| `IDataScopeProvider<TEntity>.ResourceName` / `.ScopeName` | 本 Provider 负责的资源与范围 |
| `IDataScopeProvider<TEntity>.BuildPredicateAsync(context, ct)` | 构造该范围的查询谓词；"全部可见"显式返回 `_ => true`，"不贡献可见性"返回 `_ => false` |
| `IDataScopeAssignmentProvider.GetAssignmentsAsync(subject, resourceName, operation, ct)` | 业务实现：取当前主体在该资源该操作上的全部范围分配 |
| `IDataScopeApplier.ApplyAsync(query, resourceName, operation, ct)` | 把可见范围合并进查询，返回施加范围后的 `IQueryable<TEntity>` |
| `AddDataScopeCore()` | 注册 `IDataScopeApplier`（Scoped） |
| `AddDataScopeProvider<TEntity, TProvider>()` | 注册某实体的一种范围 Provider（Scoped） |

## 实现行为

`DefaultDataScopeApplier` 的组合规则：

1. 主体不可识别：返回空结果集（`Where(_ => false)`），默认拒绝而不是放行全部。
2. `IsSuperAdmin`：原样返回查询，不施加范围。
3. 没有任何分配：返回空结果集。
4. 分配了一个没有对应 Provider 的范围：跳过该分配，不放宽范围。
5. 多个分配之间取并集（OR）。
6. 谓词不可空：`_ => true` 表示“全部可见”，`_ => false` 表示“本范围不贡献可见性”。

## 注意事项

- 谓词必须可被数据库翻译，不要依赖客户端求值或先加载候选。在关系型 Provider 上编写测试：EF Core InMemory 不验证可翻译性。
- 列表、总数、导出、批量必须共用同一个范围入口，否则分页总数与实际可见数据不一致。
- 读和写可以用不同范围：`DataScopeAssignment` 带 `Operation` 维度。
- 租户隔离、软删除等全局查询过滤器始终与业务范围取交集，不会被这里的并集放宽。
- 不要把组织、负责人这类集合关系展开成资源 ACL；文档分享这类一次性授予也不适合做成范围。
- 同一资源既有范围又有 ACL 时，组合公式见[资源实例授权](./authorization-resource.md#将-acl-合并进列表)：`(数据范围 OR ACL 允许) AND NOT ACL 拒绝`。
  本组件只承担 `OR` 的那一半，`ApplyAsync` 之后仍须在同一查询入口扣除 ACL 拒绝集合。ACL 允许并入并集时可写成一个 `IDataScopeProvider`，主体还需拿到对应 `ScopeName` 的分配。
- `IPermissionSubjectProvider` 与 `IDataScopeAssignmentProvider` 都没有默认实现，必须由业务项目提供。

## 相关

- [功能权限](./authorization.md)
- [资源实例授权](./authorization-resource.md)
- [DDD 基座](../ddd-struct/ddd-struct.md)
