# 对象映射

把「A 类型的字段拷贝到 B 类型」统一抽象为一个服务：业务代码只依赖 `IObjectMapper.Map`，具体实现由宿主在组合根注册，当前提供 Mapster 实现。

## 何时使用

| 场景 | 推荐 |
| --- | --- |
| 需要对象映射 | `Leistd.ObjectMapping.Mapster` |
| 仅在领域/应用层依赖映射抽象编写业务代码 | 只引用 `Leistd.ObjectMapping.Core` |
| 需要把映射下推到数据库查询 | **不用本组件**——写显式 `Select` 投影，意图更清楚，也不受映射配置变化的影响 |

> 业务代码依赖 `IObjectMapper`，不直接依赖 `Mapster.IMapper`。

## 安装

```bash
dotnet add package Leistd.ObjectMapping.Core

dotnet add package Leistd.ObjectMapping.Mapster
```

## 注册

在 `Program.cs` 注册实现，它把自己绑定到 `IObjectMapper`（Singleton），并注册底层的 `IMapper`：

```csharp
builder.Services.AddMapsterObjectMapper(options =>
{
    options.Configurators.Add(config => config.Scan(typeof(OrderMappings).Assembly));
    options.ValidateMappings = true;
});
```

映射配置用 Mapster 官方的 `IRegister` 书写，`config.Scan(...)` 把程序集里的注册类登记到组件自己的 `TypeAdapterConfig`：

```csharp
using Mapster;

public class OrderMappings : IRegister
{
    public void Register(TypeAdapterConfig config) =>
        config.NewConfig<Order, OrderDto>()
            .Map(dest => dest.CustomerName, src => src.Customer.Name);
}
```

`configure` 参数可选，省略时使用默认配置（无映射规则）。

## 使用

注入 `IObjectMapper`，调用 `Map` 完成转换：

```csharp
public class OrderMapping(IObjectMapper mapper)
{
    public OrderDto ToDto(Order order)
        => mapper.Map<Order, OrderDto>(order);

    public void Apply(UpdateOrderDto dto, Order order)
        => mapper.Map(dto, order);

    public List<OrderDto> ToDtos(IEnumerable<Order> orders)
        => mapper.MapList<Order, OrderDto>(orders);
}
```

批量映射 `MapList` 是 `Leistd.ObjectMapping.Extensions` 命名空间下的扩展方法，需 `using Leistd.ObjectMapping.Extensions;`。

需要把“只查目标字段”下推到数据库时，写显式 `Select`，不要走映射库：

```csharp
IQueryable<OrderDto> query = orders
    .Where(o => o.IsPaid)
    .Select(o => new OrderDto { Id = o.Id, Total = o.Total });
```

查询投影使用显式 `Select`，避免映射配置静默改变 SQL 列清单。

书写规则：

- 业务服务只注入 `IObjectMapper`。
- 能按名称约定映射的不写配置；只为不同名、需计算或需忽略的成员写 `IRegister`。
- 复杂转换（跨多个来源、带业务判断）写普通方法，不塞进映射表达式。
- 配置里的嵌套映射交给 Mapster 按同一份配置递归完成。不要在配置里调用无参 `Adapt<T>()`：它用 `TypeAdapterConfig.GlobalSettings`，组件登记的规则在那里静默失效。

## 接口参考

`Leistd.ObjectMapping.Core` 包的 API 位于 `Leistd.ObjectMapping.Abstractions` 与 `Leistd.ObjectMapping.Extensions`：

| 成员 | 说明 |
| --- | --- |
| `IObjectMapper` | 对象映射器统一接口 |
| `IObjectMapper.Map<TSource, TDestination>(source)` | 映射并创建新的目标实例 |
| `IObjectMapper.Map<TSource, TDestination>(source, contextItems)` | 带上下文数据（`IDictionary<string, object>`）的映射，供自定义解析器读取 |
| `IObjectMapper.Map<TSource, TDestination>(source, destination)` | 映射到现有目标实例（就地更新），返回该实例 |
| `ObjectMapperExtensions.MapList<TSource, TDestination>(sources)` | 扩展方法，批量映射为 `List<TDestination>`；`mapper`/`sources` 为 null 抛 `ArgumentNullException` |

## 实现行为

### Leistd.ObjectMapping.Mapster

- 以 Singleton 注册 `IMapper`（`new Mapper(config)`）：基础配置 `config.Default.PreserveReference(true)` 启用循环引用保护。
- `MapsterOptions.Configurators` 中的每个委托接收 `TypeAdapterConfig`；`ValidateMappings = true` 时在启动阶段调用 `config.Compile()` 提前编译并校验。
- 映射配置在组件自己的 `TypeAdapterConfig` 上，不修改 `TypeAdapterConfig.GlobalSettings`；程序集扫描只在宿主显式 `config.Scan(...)` 时发生。
- 带上下文的 `Map(source, contextItems)` 通过 `MapContextScope` 将上下文写入 `MapContext.Current.Parameters`。
- 额外提供 `MapsterObjectMapper.Map<TDestination>(object source)`（按运行时类型映射），不属于 `IObjectMapper` 接口，需引用具体类型才能调用。

## 配置项

`MapsterOptions`：

| 属性 | 默认值 | 说明 |
| --- | --- | --- |
| `Configurators` | 空列表 | `List<Action<TypeAdapterConfig>>`，注册映射配置委托 |
| `ValidateMappings` | `false` | 启动时是否调用 `Compile()` 编译校验 |

## 注意事项

- 业务代码应只依赖 `Leistd.ObjectMapping.Core` 的 `IObjectMapper`，不要直接引用 `Mapster.IMapper`。
- `ValidateMappings` 默认关闭；建议在开发/测试环境开启，尽早暴露未配置的映射，避免运行时才发现缺字段。
