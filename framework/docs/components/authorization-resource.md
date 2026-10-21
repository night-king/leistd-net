# 资源实例授权

资源授权判断当前主体能否对某个已加载的实例执行操作。判定走 ASP.NET Core 官方授权管线：领域规则写成官方的资源型授权处理器，ACL 与超级管理员由组件的处理器判定；拒绝优先，无结论时默认拒绝。

## 何时使用

| 场景 | 组合 |
| --- | --- |
| 按所有者、状态等领域属性判定 | `Leistd.Authorization.Resource.AspNetCore` + 官方规则处理器 |
| 支持“将这份文档分享给某人” | 追加 `Leistd.Authorization.Resource.EntityFrameworkCore` |
| ACL 需合并进列表查询 | `QueryGrantedResourceKeysAsync` / `QueryDeniedResourceKeysAsync` |

“本人的全部订单”或“本部门及下级部门”属于[数据范围](./authorization-data-scope.md)，不应展开成逐条 ACL。只检查操作类型时使用[功能权限](./authorization.md)。

## 安装

```bash
dotnet add package Leistd.Authorization.Resource.Core            # 业务入口、资源契约、ACL 存储契约
dotnet add package Leistd.Authorization.Resource.AspNetCore      # 判定实现（官方授权管线）
dotnet add package Leistd.Authorization.Resource.EntityFrameworkCore
```

仅使用领域规则时可不安装 EF Core 包。判定包以 `FrameworkReference Microsoft.AspNetCore.App` 引用官方授权组件，不依赖 `HttpContext`；Worker 等非 Web 宿主也需要 ASP.NET Core 共享框架。
实例判定必须在资源加载后执行，无法由加载前的 `[Authorize]` 策略取代。

## 注册

```csharp
using Microsoft.AspNetCore.Authorization;

builder.Services.AddSecurity();                                        // Web 宿主：当前主体来自 HTTP 请求
builder.Services.AddResourceAuthorizationEfCore<AppDbContext>();
builder.Services.AddResourceAuthorization();
builder.Services.AddScoped<IAuthorizationHandler, OrderOwnerHandler>();
builder.Services.AddScoped<IAuthorizationHandler, ArchivedOrderHandler>();
```

规则处理器按官方方式注册为 `IAuthorizationHandler`，同一资源可注册多个。仅使用领域规则时去掉 EF 那一行。

EF Core 宿主还需映射 ACL 与版本表：

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ConfigureResourceAuthorization();
}
```

ACL Store/Manager 依赖 `IDbContextProvider<TDbContext>`，因此需先注册 `AddUnitOfWork()` 与 `AddUnitOfWorkEfCore()`。业务项目也必须提供 `IPermissionSubjectProvider`。

`AddResourceAuthorization()` 与 `AddResourceAuthorizationEfCore()` 已把 ACL 写入的版本冲突登记为 409，宿主不需要另行组合；宿主 `MapCode` 可覆盖。

## 使用

### 定义资源与规则

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;

public class Order : IAuthorizableResource
{
    public string OwnerId { get; set; } = string.Empty;
    public OrderStatus Status { get; set; }
    public string ResourceKey { get; set; } = string.Empty;
    string IAuthorizableResource.ResourceName => "Orders";
}

public class OrderOwnerHandler(IPermissionSubjectProvider subjects)
    : AuthorizationHandler<OperationAuthorizationRequirement, Order>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OperationAuthorizationRequirement requirement,
        Order resource)
    {
        // 主体取被授权的 context.User，不取环境里的当前用户
        var subject = await subjects.GetSubjectAsync(context.User);
        if (subject is not null && resource.OwnerId == subject.UserId)
        {
            context.Succeed(requirement);
        }
    }
}

public class ArchivedOrderHandler : AuthorizationHandler<OperationAuthorizationRequirement, Order>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OperationAuthorizationRequirement requirement,
        Order resource)
    {
        if (requirement.Name == ResourceOperations.Update && resource.Status == OrderStatus.Archived)
        {
            context.Fail();   // 资源状态本身不允许：任何允许都推翻不了
        }

        return Task.CompletedTask;
    }
}
```

处理器不给结论时什么都不调用；`requirement.Name` 是操作名。判定规则是官方语义：

1. 没有经过认证的当前主体时，业务入口直接拒绝。
2. 任一处理器 `Fail()` 即拒绝，与处理器注册顺序无关——包括 ACL 明确拒绝（`Prohibited`，或损坏的授予值）与规则拒绝。
3. 否则任一处理器 `Succeed()` 即允许：规则允许、ACL 授予（`Granted`）、超级管理员。
4. 全部无结论时拒绝。没有 ACL 记录时 ACL 处理器不给结论，交给规则处理器。

主体的租户规则与功能权限一致，且先于超级管理员判定：租户声明非法时 ACL 处理器 `Fail()`；经 `IAuthorizationService` 显式判定别的主体时，它还须属于当前租户，否则同样 `Fail()`。
当前主体只校验声明合法。

超级管理员不会跳过领域规则的拒绝。未注册 ACL 存储时，只有规则处理器与超级管理员参与判定。ACL 按被授权的主体（`context.User`）查询，经 `IAuthorizationService` 为别的主体判权时同样成立。

### 判定单个实例

```csharp
var order = await orders.FindAsync(id, ct)
    ?? throw new BusinessException("Order:NotFound", "The order was not found.");

if (!await authorization.IsGrantedAsync(order, ResourceOperations.Update))
{
    throw new BusinessException("Order:UpdateForbidden", "You cannot update this order.");
}
```

功能权限应在控制器上先行检查，实例授权在加载后检查。宿主在 API 组合根将上述错误码分别映射为 404/403；存在性敏感的 API 可按威胁模型统一映射为 404。

本组件抛出的 `ResourceAuthorizationErrorCodes.InvalidGrant` 默认按业务异常返回 400；`ConcurrencyConflict` 由组件的注册入口登记为 409，宿主 `MapCode` 可覆盖。Core 包自带两种码的默认中英文案，宿主资源可覆盖。业务项目自己的 `Order:*` 错误码仍由宿主映射。

### 将 ACL 合并进列表

```csharp
var subject = await subjectProvider.GetCurrentSubjectAsync(ct);
var grantedKeys = await grantStore.QueryGrantedResourceKeysAsync(
    "Orders",
    ResourceOperations.Read,
    subject!.UserId,
    subject.RoleIds);

var query = dbContext.Set<Order>()
    .Where(order => grantedKeys.Contains(order.ResourceKey));
```

列表、详情、总数与导出应共用同一个查询授权入口。非超管的集合公式为：

```text
(数据范围 OR ACL 允许) AND NOT ACL 拒绝
```

`QueryGrantedResourceKeysAsync` 已在数据库中扣除 ACL 拒绝。与数据范围组合时，还需使用 `QueryDeniedResourceKeysAsync` 扣除“数据范围允许、ACL 明确拒绝”的部分。超管跳过数据范围与 ACL，但仍受租户和软删除边界约束。

集合入口不执行依赖资源状态的规则处理器。批量写操作应先按操作范围定位，再逐项执行实例判定；任一拒绝时拒绝整批。

### 替换与清理 ACL

```csharp
var current = await grantStore.GetGrantsAsync("Orders", order.ResourceKey);

await grantManager.ReplaceGrantsAsync(
    "Orders",
    order.ResourceKey,
    [
        new ResourceGrant(
            ResourceOperations.Read,
            PermissionGrantProviderNames.User,
            targetUserId,
            ResourceGrantEffect.Granted),
    ],
    current.Version,
    ct);
```

替换基于版本执行乐观并发。版本不匹配时抛 `ResourceGrantConcurrencyException` 且不写入；HTTP 宿主启用上述资源授权映射后返回 409。`null` 只应用于种子数据。

资源删除后必须清理 ACL：

```csharp
await grantManager.RemoveResourceAsync("Orders", order.ResourceKey, ct);
```

用户、角色或客户端永久删除时，同时清理功能权限与资源 ACL：

```csharp
await permissionGrantManager.RemoveProviderAsync(
    PermissionGrantProviderNames.User,
    userId,
    ct);
await grantManager.RemoveProviderAsync(
    PermissionGrantProviderNames.User,
    userId,
    ct);
```

永久删除主体时必须清理 ACL，避免标识复用后遗留授权重新生效；软删除不清理。

## 接口参考

| 类型 | 用途 |
| --- | --- |
| `ResourceOperations` | 内置 `Read`、`Update`、`Delete` 和 `Share` 操作名 |
| `IAuthorizableResource` | 通过 `ResourceName` 和 `ResourceKey` 自述实例 |
| `IResourceAuthorizationService` | 业务入口：以当前主体对已加载资源判定（Core 契约，实现在 AspNetCore 包） |
| `AddResourceAuthorization()` | AspNetCore 包：注册业务入口、ACL 处理器与官方授权核心服务 |
| `ResourceOperationRequirement` | 官方 `OperationAuthorizationRequirement` 的派生，带 ACL 定位（资源名、Key）；规则处理器按 `OperationAuthorizationRequirement` 匹配 |
| `IResourceGrantStore` | 读取 ACL、有效效果和可翻译的资源 key 查询 |
| `IResourceGrantManager` | 替换 ACL，清理资源或主体的 ACL |
| `ResourceGrant` / `ResourceGrantSet` | 单条 ACL 与某实例的完整 ACL 快照 |

下表状态码是组件登记的默认值，宿主可用 `MapCode` / `MapException` 覆盖；Core 异常本身不决定 HTTP。ACL 冲突的默认 409 由本组件的注册入口登记，`UnstableGrantSnapshotException` 的默认 503 由功能授权组件的 `AddPermissionAuthorizationCore()` 登记。

| 异常 | 建议 HTTP | 含义 |
| --- | --- | --- |
| `InvalidResourceGrantException` | 400 | 主体或授予效果无效 |
| `ResourceGrantConcurrencyException` | 409 | 保存基于过期版本 |
| `UnstableGrantSnapshotException` | 503 | 连续重读仍无法获得一致快照 |

## 实现行为

- ACL 唯一键为 `(ResourceName, ResourceKey, Operation, ProviderName, ProviderKey)`，单个主体对同一实例操作只有一个效果。
- `QueryGrantedResourceKeysAsync` 在数据库内完成允许集合减拒绝集合，并返回去重的 `IQueryable<string>`。
- `ResourceAuthorizationVersionRecord.Version` 是 EF Core 并发令牌。全量替换只在 ACL 实际变化时递增版本。
- `RemoveProviderAsync` 推进受影响资源的版本，防止旧快照写回已删除授予。
- `ResourceGrantEffect` 以字符串持久化，只有已定义的 `Granted` 会产生允许；非法值失败关闭。

## 注意事项

- 单实例必须先加载再判定，列表必须在数据库查询中合并 ACL，不逐行加载后判定。
- 同一个 Read 只判定一次；详情经统一可见查询定位后，不再重复调用实例判定。
- 影响读取可见性的规则必须有可翻译的等价谓词；否则只用于加载后的领域不变量。
- 业务实体应持有字符串资源 key；在查询中对 Guid 调用 `ToString()` 是否可翻译取决于 Provider。
- 通用 ACL 表无法对任意业务表建立外键。资源删除后调用 `RemoveResourceAsync`，并定期清理孤儿记录。
- `IPermissionSubjectProvider` 没有默认实现，且是 `DefaultPermissionChecker` 的必需构造参数：未注册时解析权限校验器直接失败，而不是判定为拒绝。

## 相关

- [功能权限](./authorization.md)
- [数据范围](./authorization-data-scope.md)
- [当前用户与身份信息](./security.md)
