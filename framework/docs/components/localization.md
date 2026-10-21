# 多语言本地化（JSON 资源）

本地化组件从程序集内嵌的 `{culture}.json` 加载文案，并将未登记的强类型资源委派给 .NET RESX 工厂。

## 何时使用

| 场景 | 用法 |
| --- | --- |
| Web API 需按 Accept-Language 请求头返回本地化文案 | 引用 `Leistd.Localization.AspNetCore`，`AddJsonLocalization` + `UseJsonRequestLocalization` |
| 领域/类库层只需注入 `IStringLocalizer` 查文案，不依赖 ASP.NET Core | 只引用 `Leistd.Localization.Core` |
| 业务项目要覆盖或追加框架默认文案 | 在自己的程序集放同名键的 `{culture}.json` 并登记该程序集 |
| 与 `Leistd.ExceptionHandling` 配合，让错误消息本地化 | `BusinessException("模块:键", safeMessage)` 在构造时给出词条键，`WithData` 传占位参数 |

未注册本地化或词条未命中时，业务异常直接回落到构造时的安全 `Message`。字段校验错误使用自身消息。

## 安装

```bash
dotnet add package Leistd.Localization.Core
dotnet add package Leistd.Localization.AspNetCore
```

## 注册

在 `Program.cs` 注册本地化并接入请求 culture 中间件：

```csharp
builder.Services.AddJsonLocalization(options =>
{
    options.SupportedCultures = ["zh-CN", "en"];
    options.ResourceAssemblies.Add(typeof(Program).Assembly);
    options.JsonResourceTypes.Add(typeof(MyResourceMarker));
});

var app = builder.Build();

app.UseJsonRequestLocalization();
```

`SupportedCultures` 是支持语言的唯一来源：首项即默认语言，同时决定请求默认区域性与资源回落语言；列表为空或含无效文化名时启动失败。无参 `IStringLocalizer` 读取 JSON；`IStringLocalizer<T>` 只有在 `T` 已加入 `JsonResourceTypes` 时读取 JSON，否则使用 `ResourceManagerStringLocalizerFactory`。`UseJsonRequestLocalization` 应放在所有读取当前 culture 的中间件之前。

## 资源文件

资源为嵌入程序集的 JSON，按 `{ResourcesPath}/{culture}.json`（默认 `Resources/`）命名，`EmbeddedResource` 打包：

```jsonc
// Resources/en.json
{
  "culture": "en",
  "texts": {
    "Order:StockInsufficient": "Out of stock: {Sku}"
  }
}
// Resources/zh-CN.json
{
  "culture": "zh-CN",
  "texts": {
    "Order:StockInsufficient": "库存不足：{Sku}"
  }
}
```

```xml
<!-- csproj -->
<ItemGroup>
  <EmbeddedResource Include="Resources\*.json" />
</ItemGroup>
```

- 缺少 `culture` 的文件会被忽略。
- 同一 culture 的键按 `ResourceAssemblies` 顺序合并，后登记的程序集覆盖前者。
- 无效 JSON 会被跳过并记录 Warning；文件名与 `culture` 不一致时仍加载，但记录 Warning。
- 启动服务按支持语言预热资源缓存。

## 使用

发出错误码的组件在自己的 `Add*` 里登记默认译文，宿主不必知道组件有哪些词条：

```csharp
services.AddJsonLocalizationResources(typeof(OrderService).Assembly);
```

注入 `IStringLocalizer` 按键取文案：

```csharp
using Microsoft.Extensions.Localization;

public class OrderNotifier(IStringLocalizer localizer)
{
    public string StockWarning() => localizer["Order:StockInsufficient", "A1"];
}
```

按当前 `CultureInfo.CurrentUICulture` 逐级回落（`zh-Hans-CN` → `zh-Hans` → `zh`），再回落到默认语言；仍未命中则返回键本身，`ResourceNotFound` 为 `true`。

## 接口参考

| 成员 | 说明 |
| --- | --- |
| `JsonStringLocalizer` | `IStringLocalizer` 实现，按 culture 回落查嵌入 JSON，未命中返回键本身 |
| `JsonStringLocalizerFactory` | `IStringLocalizerFactory` 实现，`Create(Type)` / `Create(string,string)` 返回同一共享视图 |
| `JsonLocalizationResourceReader` | 读取并缓存嵌入 JSON，按程序集登记顺序合并键 |
| `JsonLocalizationOptions.ResourceAssemblies` | 承载嵌入 JSON 的程序集集合，后登记者覆盖前者 |
| `JsonLocalizationOptions.ResourcesPath` | 嵌入资源逻辑目录，默认 `Resources` |
| `JsonLocalizationOptions.SupportedCultures` | 支持语言，首项为默认/回落语言，默认 `["en","zh-CN"]` |
| `JsonLocalizationOptions.DefaultCulture` | 只读，`SupportedCultures` 的首项 |
| `AddJsonLocalizationResources(assembly)` | Core 包：把组件程序集嵌入的 `Resources/{culture}.json` 登记为资源，恒插在最前，宿主资源总能覆盖；幂等 |
| `AddJsonLocalization(configure?)` | 注册 JSON localizer 栈，按 `SupportedCultures` 配置请求区域性；重复调用不重复登记，各次 `configure` 依次应用 |
| `UseJsonRequestLocalization()` | 接入请求 culture 解析中间件（查询参数、Cookie、Accept-Language 请求头） |

## 配置项

`JsonLocalizationOptions`：

| 属性 | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `ResourceAssemblies` | `IList<Assembly>` | 空（`Add*` 时登记） | **加载**：承载嵌入 JSON 的程序集；后登记者覆盖前者 |
| `JsonResourceTypes` | `ISet<Type>` | 空 | **路由**：显式登记走 JSON 的 typed 资源标记类型；未登记的 `IStringLocalizer<T>` 走官方 RESX |
| `ResourcesPath` | `string` | `Resources` | 嵌入资源相对程序集根的逻辑目录 |
| `SupportedCultures` | `IList<string>` | `["en","zh-CN"]` | 支持语言，首项为默认/回落语言；为空或含无效文化名时启动失败 |
| `DefaultCulture` | `string`（只读） | `en` | `SupportedCultures` 的首项 |

## 注意事项

- 未调用 `AddJsonLocalization` 时，异常处理器遵循自身的安全回退规则，不会无条件公开异常消息。
- 资源 JSON 必须 `EmbeddedResource`；仅作为 `Content` 不会被读取。
- 文案键必须全局唯一；JSON 工厂对所有已登记类型提供同一合并视图。

## 相关

- [业务异常与全局异常处理](./exception-handling.md)（错误消息本地化）
- [核心基础库](./core.md)
