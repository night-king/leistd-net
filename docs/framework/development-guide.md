# Leistd 框架开发规范

本文档面向**人与 AI**：在 `framework/` 内新增或修改组件时，必须遵循以下约定，以保证框架的一致性、可打包性与可调试性。

---

## 1. 命名与分组

- 程序集 / 包名：`Leistd.<领域>[.<实现>]`
  - 领域抽象核心：`Leistd.<领域>.Core`（如 `Leistd.Lock.Core`）
  - 具体实现：`Leistd.<领域>.<技术>`（如 `Leistd.Lock.Redis`、`Leistd.ObjectMapping.Mapster`）
  - ASP.NET Core 集成：`Leistd.<领域>.AspNetCore`
- 目录归属：
  - 共享组件放 `framework/components/<kebab-分组>/Leistd.Xxx/`，分组与现有保持一致。新分组用 kebab-case。
  - DDD 基础类型放 `framework/ddd-struct/Leistd.Ddd.Xxx/`。
- `PackageId` 默认等于项目名（= 程序集名），**无需**在 csproj 显式设置。
- 包名表达分发边界，命名空间表达概念；`.Core` 等抽象打包后缀不进入命名空间，具体规则见下文。

- 注册入口命名约束新增 API；现有公共 API 不为名称整齐而改名。

  | 情形 | 命名 | 例 |
  | --- | --- | --- |
  | 家族主要公共能力 | `Add{Family}`，由提供该能力的包承担（多为 Core）；运行前提由组件契约声明、宿主另行注册 | `AddNotifications`（另需 `INotificationStore` 实现）、`AddRealTime`、`AddUnitOfWork` |
  | 宿主包补全家族 | 宿主包用 `Add{Family}`，Core 包用 `Add{Family}Core` | `AddMultiTenancy` / `AddMultiTenancyCore`、`AddCorrelationId` / `AddCorrelationIdCore` |
  | 附加技术包 | `Add{Family}{Tech}` | `AddNotificationsEfCore`、`AddRealTimeSignalR` |
  | 实现变体 | `Add{Impl}{Capability}` | `AddRedisDistributedLock`、`AddSmtpEmailSender`、`AddMapsterObjectMapper` |

  注册扩展类命名为 `DependencyInjection`，放在包根。

- 配置节以 `Leistd:` 为根，按家族、实现、命名实例分层：`Leistd:UnitOfWork`、`Leistd:Lock:Redis`、`Leistd:ServiceClients:{Name}`。

- 命名空间 = `RootNamespace` + 目录路径（IDE0130）。包名以 `.Core` 结尾的显式声明剥掉 `.Core` 的 `RootNamespace`（`Leistd.Core` → `Leistd`），其余项目不声明；目录名不得与包名重复（`Leistd.Lock.Memory.Locks`）或使用缩写（`Uow`）。由 `scripts/check-csproj-conventions.py` 强制。

- **目录按职责判断，不按类名后缀判断。** 包内通常同时有内容目录（`Checking/`、`Grants/`）与类型目录
  （`Dtos/`、`Errors/`、`Options/`），这种混合组织合法；新类型放进与现有同类最接近的目录。

- **包内主体有两种合法形态，按家族有没有并列的子话题来选：**

  | 家族形态 | 组织方式 | 例 |
  | --- | --- | --- |
  | 有并列子话题 | **按内容分**：契约与实现同处一个内容目录 | `Leistd.Authorization.Core` 的 `Checking/`、`Definitions/`、`Grants/`；`Leistd.MultiTenancy.Core` 的 `Context/`、`ConnectionStrings/`、`Management/`；`Leistd.Security.Core` 的 `Users/`、`Claims/`、`Clients/` |
  | 只有单一中心概念 | **按层次分**：`Abstractions/` 放契约、`Services/` 放实现 | `lock`、`event-bus`、`object-mapping` 等小型家族 |

- `Services/` 只放实现；契约归 `Abstractions/` 或内容目录。它们只适用于真正的单概念小包，
  不是新类的默认投放点；一旦同时出现定义、授权、管理、存储等并列语义，就改为内容目录。

- 扩展类在按层次分的包中放 `Extensions/`；按内容分的包中与被扩展类型同目录。

- `Filters/` 按所属过滤语义组织：MVC/Hub 管道过滤器（如 `Leistd.Response.AspNetCore.Filters`）、
  数据过滤状态（`Leistd.Data.Filters`）或通知投递筛选（`Leistd.Notifications.Filters`）。
  其他名称以 `Filter` 结尾的类型放所属内容目录，不据后缀归类。
- 端点映射：组件只映射一个 Hub 时，`Map*Hub` 随注册入口放包根 `DependencyInjection.cs`；映射多个 Minimal API 端点的放 `Endpoints/`
  （如 `Leistd.Settings.AspNetCore.Endpoints`）。
- 事件类型（以 `Event` 结尾）放 `Events/`，事件处理器（以 `EventHandler` 结尾）放 `EventHandlers/`，
  与 `Leistd.EventBus.Events`、`Leistd.EventBus.EventHandlers` 同一写法。
- 周期任务（`*Job`）与常驻消费者（`*Worker`）**不单设 `Jobs/` / `Workers/`**，放在它服务的内容目录里，与同一功能的选项、服务同处
  （如 `Leistd.Notifications.EntityFrameworkCore` 的 `Retention/NotificationRetentionJob`、`Leistd.BackgroundJobs.InProcess` 的 `Queues/BackgroundTaskQueueWorker`）；
  同类成熟框架的清理任务也跟随所属功能目录。模板业务项目的归类见模板后端规范。

- **不要让命名空间与其中的类型同名**（FDG 明确禁止）。家族名与核心类型同名时，
  给实现类加 `Default` 前缀（`Leistd.UnitOfWork.DefaultUnitOfWork`），与
  `DefaultPermissionChecker` / `DefaultDataScopeApplier` 一致。

- **家族名不要遮蔽 BCL 类型**。异常处理家族使用 `Leistd.ExceptionHandling`，避免 `Exception` 命名空间遮蔽 `System.Exception`。

---

## 2. csproj 模板

新组件的 csproj 应尽量精简——共享属性由 `Directory.Build.props → common.props` 自动注入，**不要**重复声明 `LangVersion`、`Nullable`、`ImplicitUsings`、`GenerateDocumentationFile`、版本、打包元数据、Source Link。

最小示例：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <!-- 仅在与程序集名不同时设置：<RootNamespace>Leistd.Xxx</RootNamespace> -->
  </PropertyGroup>

  <ItemGroup>
    <!-- 第三方包：version-less，由 CPM 统一版本 -->
    <PackageReference Include="SomeThirdParty" />
  </ItemGroup>

  <ItemGroup>
    <!-- 框架内部引用：相对路径 ProjectReference -->
    <ProjectReference Include="..\..\core\Leistd.Core\Leistd.Core.csproj" />
  </ItemGroup>

</Project>
```

要点：
- **不重复声明 `common.props` 已注入的属性**（`LangVersion` / `ImplicitUsings` / `Nullable` /
  `GenerateDocumentationFile`）。写成与继承值**不同**的值是合法覆盖（测试项目的
  `GenerateDocumentationFile=false` 就是），同值重复则由 `check-csproj-conventions.py` 拦下：
  重复声明会让改共享值只对没重复的项目生效，差异静默存在。
- **`PackageReference` 一律不写 `Version`**——版本在 `framework/Directory.Packages.props` 用 `<PackageVersion>` 声明。新引入的第三方包必须先在该文件登记版本，否则还原报错（CPM 已启用）。
- ASP.NET Core 能力用 `<FrameworkReference Include="Microsoft.AspNetCore.App" />`，不要直接引 Microsoft.AspNetCore.* 包。
  只需要契约的 `*.Abstractions` 纯抽象包除外：它不含 Web 运行时，直接 `PackageReference`
  才能让非 Web 的包也用上（如 §6.1 的 `Microsoft.AspNetCore.DataProtection.Abstractions`）。
- 纯内部、不应发布的项目，在其 csproj 设 `<IsPackable>false</IsPackable>`（默认全部可打包）。

---

## 3. 加入解决方案

新项目创建后，加入框架解决方案：

```bash
dotnet sln framework/Leistd.Framework.slnx add framework/components/<分组>/Leistd.Xxx/Leistd.Xxx.csproj
```

`.slnx` 会按目录自动归入对应解决方案文件夹。

---

## 4. 文档注释

- `GenerateDocumentationFile` 已全局开启，**公共 API 必须写 XML 文档注释**（`///`）。缺注释（CS1591）是构建错误。
- **语言约定**：XML 与代码注释用**中文**；异常消息与日志消息面向运维，**统一用英文**。
- **联系方式不进日志**：邮箱、手机号这类能直接触达到人的值，写进日志前先经
  `Leistd.Redaction.TextRedactor` 脱敏（`zhangsan@example.com` → `zha***@example.com`），
  或改记标识符。日志通常被集中采集、保留更久、可见范围更大，还会被前端错误上报之类的旁路带走。
  **也不要与标识符同行记**：用户名常常就是邮箱本地部，同一行给出本地部与域名等于把脱敏拼回去。
  账号名本身可以记——它不是联系方式，且是这些日志可读性的来源。
  `BusinessException` 的 message 可能随异常对象进日志（5xx 的异常链、宿主自己记录的异常），不拼入联系方式，
  这类值交给 `WithData`：它只填返回给调用方的 `detail` 与操作记录，框架的异常日志不记、审计日志只记参数名。
  由 `scripts/check-contact-info-logging.py` 守住（判据是实参表达式与插值洞，不是占位符名）。
- **官方日志脱敏（`Microsoft.Extensions.Compliance.Redaction`）只在"必须输出个人数据且要集中管控策略"时采用**，
  实测（extensions 10.9）有三处反直觉：普通模板日志不会被脱敏，须改写为带数据分类的 `[LoggerMessage]`；
  `services.AddSerilog(configure)` 与 `EnableRedaction()` 同用会让日志全部消失，须改用 `builder.Logging.AddSerilog(logger)`；
  宿主漏调 `EnableRedaction()` 时被分类的参数输出为空，不泄露原文。HMAC 脱敏器仍是实验性 API。
- `cref` 必须可解析（避免 CS1574）。
- 组件用法文档位于 `framework/docs/components/` 与 `framework/docs/ddd-struct/`，骨架见 §4.3。

### 4.1 信息分层

参考 Microsoft 的 [XML 文档建议](https://learn.microsoft.com/dotnet/csharp/language-reference/xmldoc/recommended-tags)与[命名指南](https://learn.microsoft.com/dotnet/standard/design-guidelines/naming-guidelines)：优先用名称和类型表达职责，注释补充非显然契约。本仓库要求公共 API 提供 `<summary>` 或显式继承文档，并采用以下分工：

| 内容 | 位置 | 约束 |
| --- | --- | --- |
| API 是什么 | `<summary>` / `<inheritdoc/>` | 一句话或继承既有契约；全部公开成员 |
| 参数、返回值与异常 | `<param>`、`<returns>`、`<exception>` | 只补签名无法表达的信息 |
| 前置条件、失败形态、顺序、线程和生命周期 | `<remarks>` | 只写会改变正确用法的契约 |
| 主要注册入口和非显然主路径 | `<example>` + `<code>` | 使用真实、可编译的最小示例 |
| 组件安装、注册、默认行为和限制 | `framework/docs/` | 面向消费者，不复制 XML |
| 看似可删但必须保留的实现约束 | 行内 `//` | 独占一行，通常 1–3 行 |
| 实施过程和历史 | Git、PR、CI | 不写入源码和分发文档 |

统一规则：名称与类型先表达职责，注释只补正确使用所需的契约；文档只描述当前产品。

| 对象 | 保留 | 精简 |
| --- | --- | --- |
| 公共类型与成员 | `<summary>` 或显式 `<inheritdoc/>`；单位、默认值、空值与 `false` 的含义、失败形态、取消、顺序、作用域、租约、幂等、事务、租户、部署前提 | 名称翻译式套话、构造参数逐字复述、与组件文档重复的实现说明 |
| 内部成员与行注释 | 算法原因、并发与安全不变量、外部约束 | 步骤编号、代码复述、装饰分隔线、修复经过与设计演进 |
| 组件文档 | 安装与注册入口、最小示例、关键 API、配置、运行限制 | 过程记录、实现教学、夸张警示、与其他文档重复的事实 |

- 精简以语义为单位，不按行数截断或批量删 `<remarks>`；删除前对照源码确认该句不是上表的契约。
- `<summary>` 保持一句话：短摘要的起止标签与正文同一行；正文确需换行时，起止标签各占一行。补充契约放 `<remarks>`；属性不机械加“获取或设置”；参数、返回值只补签名之外的信息。
- 同一成员使用 `<param>` 时覆盖全部参数，避免 CS1573；整组都无额外信息时删除。必要约束也可合并进摘要或 `<remarks>` 后删整组，不抑制文档警告。
- `<remarks>` 保留影响正确用法的契约；组件文档说明必要的使用取舍，过程记录交给 Git。
- `<example>` 用于主要注册入口和容易误用的主路径；可从签名直接推出的调用不补示例。
- 接口或基类定义公共契约；实现确有继承且语义一致时使用 `<inheritdoc/>`，不复制同一段说明；实现收窄或改变语义时写自己的摘要。
- 随包 XML 不会自动展开 `<inheritdoc/>`；目标须可追溯到本包、依赖包的 XML 或 BCL，AI 读取时继续解析目标契约。
- XML 不使用 Markdown `**…**`；行内代码用 `<c>`，引用 API 用 `<see cref="..."/>`。
- `<para>` 仅用于两个以上段落。
- 行内注释解释“为什么必须这样”，不复述代码正在做什么。工具指令（`#pragma`、`#if`、`SuppressMessage` 的 `Justification`）与版权声明不在精简范围。
- 中文注释使用中文标点；XML、JSDoc 摘要以句号结束，其他注释的完整句以句号结束。代码标识、路径和命令保留原样；仅调整新增或修改的注释。
- 示例中的 API、依赖和变量必须可用；XML 内的代码不会自动参与 C# 编译，关键路径需单独验证。

示例：`HubIdentityOptions.RevalidationInterval` 的摘要补充默认值与 `null` 的含义；配置方式等补充契约放在 `<remarks>` 中。

```csharp
/// <summary>两次复评之间的最小间隔；为 <see langword="null"/>（默认）时每次调用都复评。</summary>
public TimeSpan? RevalidationInterval { get; set; }
```

Microsoft 没有规定注释密度、`<remarks>` 行数或示例配额。本仓库也不为这些数字设硬闸门；统计只用于发现趋势，审查仍回到必要性、准确性与唯一性。

`summary` 的单行与多行都是合法格式，微软[语言规范 D.3.16](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/documentation-comments#d316-summary)同时示例两种写法；以上换行规则是本仓库的简约风格约定。

### 4.2 由闸门保证的部分

以下判据由构建或 `check-all.ps1` 保证：

| 判据 | 由谁保证 |
| --- | --- |
| 公共可见成员必须有 XML 注释 | 编译器 `CS1591`（`common.props` 未将其放入 `NoWarn`，且 `TreatWarningsAsErrors` 开启） |
| Framework 的 `///` 不挂 private / internal 成员 | `scripts/check-doc-comment-shape.py` 规则 1 |
| `///` 内不写 Markdown `**…**` | 同上，规则 2（`/api/health/**` 这类 URI 通配不误伤） |
| 组件文档必选段齐全、顺序正确、名称精确、无空壳段 | `scripts/check-docs-skeleton.py` |
| 分发面不出现路线图 / 升级动作表述 | `check-retired-terms.ps1` 的 `roadmap` 规则 |

`check-doc-comment-shape.py` 同时输出 `<summary>`、`<remarks>` 和 `<example>` 数量，仅供趋势观察。Template 只执行 XML 渲染形态检查。分发面不得出现路线图或升级过程；校验脚本必须提供自测，并在规则不再防止现实错误时删除。

### 4.3 组件文档骨架

组件文档是消费者与 AI 的入口，**标题固定才能被精确定位**，因此骨架是规范而非建议。

**必选段，固定顺序、固定名称：**

```
# <概念标题>            —— 不写包名，写它解决的问题
   导语 1 段，≤120 字：这个组件解决什么问题，怎么解决
## 何时使用             场景 → 用法 表格
## 安装                 dotnet add package
## 使用                 最小可运行示例
## 接口参考             关键类型与入口；精确签名交给随包 XML
## 注意事项             用错会出事的点
```

**可选段，有才出现、无则整段不写：**

| 段 | 出现条件 |
| --- | --- |
| `## 注册` | 需要注册服务、中间件、过滤器或端点 |
| `## 配置` | 需要在多个实现间选择或组合 Provider |
| `## 配置项（<配置节路径>）` | 有 Options 类（无绑定节路径时只写 `## 配置项`） |
| `## 实现行为` | 有非显然的运行时语义 |
| 家族特有段 | 该家族独有的话题 |
| `## 相关` | 有内容真正相关的兄弟文档 |

**顺序规则：必选段之间的相对顺序固定；可选段按读者需要它的时机插入；`## 注意事项` 与 `## 相关` 恒在最后两段。**

- **没有配置项就不写 `## 配置项`**，不写「当前无配置项」。空壳段只消耗读者的目录，不提供信息。
- **`## 相关` 里不放恒定链接**。每篇都指向组件总览与依赖注入等于没有指向；无真正相关的兄弟文档时整段删除。
- **同一事实在一篇文档里只写一次**。放在读者最可能找它的那一段——通常是 `注意事项`。
- 示例代码受 §5.1 的依赖方向约束。

---

## 5. 依赖方向（不可违反）

- `Leistd.<...>.Core` / `Leistd.Ddd.Domain` 是底层，**不得**反向依赖上层或具体实现。
- `components` 可被 `ddd-struct` 依赖；**`components` 不得依赖 `ddd-struct`**（单向）。在 `ddd-struct` 内部，`Infrastructure` 只直接依赖 `Domain`，`Application` 只直接依赖 `Application.Contracts`，`Application.Contracts` 只依赖 `Leistd.Data`；其余能力各层直接引用对应组件。
- **`*.Core` 不得依赖"可替换的"具体技术**：Web 宿主（ASP.NET Core）、ORM（EF Core）、消息中间件等只能出现在对应实现层（`*.AspNetCore*`、`*.EntityFrameworkCore`）。判据是**能不能换掉而组件仍成立**——工作单元不接 EF 仍能提供边界与阶段，授权不接 ASP.NET 仍能判权，所以那些必须外移。
  - **例外：某项技术就是该组件主要 API 的实现机制本身时，它属于 Core。** 目前只有动态代理属于这一类，且只涉及一个包：`Leistd.UnitOfWork.Core`（`[UnitOfWork]`、`[UnitOfWorkEventHandler]`）。这些声明式特性的语义**就是**"由拦截器织入"，把代理拆出去会得到一个无法提供其主要能力的 Core——命名会更整齐，但包不再自洽。
  - 例外是**闭集**，不是逃生门：新增组件不得自行扩列。确有需要时先改本条规范并说明为什么该技术不可替换，再落代码。
  - 例外不放宽平台无关：这个包依旧不引 Web 与 ORM。
  - **Core 包只依赖抽象**：`PackageReference` 限于 `Microsoft.Extensions.*` 与 `*.Abstractions`，不得引用 `.AspNetCore` / `.EntityFrameworkCore` 等宿主与基础设施包。业务项目在架构门禁里限制应用层、领域层能引用什么，而 Core 的依赖会顺着传递引用进它们的闭包。跨家族的 Core → Core 引用不禁止（否则会禁掉"错误码译文随包分发"这类能力），被引用方受同一条约束，传递进去的仍然只有抽象。由 `scripts/check-csproj-conventions.py` 强制。
  - 身份等概念在 `*.Core` 抽象里用中立类型（`string userId` / `ClaimsPrincipal`），不要把富身份模型（如 `ICurrentUser`）焊进核心接口签名；带技术细节的默认值（如 claim 类型）由宿主层注入而非写死在 Core。
- 组件只注册完成自身功能必需、且在组件契约中声明的依赖；选哪个实现、映射哪些业务端点、如何编排应用管道由宿主决定（如通知组件不代映射实时 Hub），宿主显式调用各自的 `Add*` / `Map*` / `Use*` 组合。注册方式见 §6.6。
- 新增跨域依赖前先评估是否会引入环，框架解决方案编译会暴露环依赖。

### 5.1 文档示例也受依赖方向约束（组件示例自包含原则）

依赖方向不仅约束代码，**也约束文档示例**——组件文档（`framework/docs/components/<家族>.md`）的示例代码，只能使用**该组件自身的公共 API + 原生 .NET / EF Core 类型**，**不得**引用它并不依赖的其它组件或 `ddd-struct` 的类型。

- 具体地，组件示例里**禁止出现** `ddd-struct` 专属类型：`IRepository<>`、`BaseAppService`、`IAppService`、`Entity<>`、`FullAuditedEntity<>`、`GetQueryableAsync()` 等。分页请求与结果（`PageRequest` / `PagedResult<T>`）属于 `Leistd.Data`，组件依赖它时示例里可以使用。因为 `components` 不依赖 `ddd-struct`（见上），示例若用了这些类型，就等于让上游组件的文档倒挂到下游，破坏组件独立闭环。
- **EF Core 集成组件**（`auditing`、`unit-of-work` 等）示例中出现原生 `DbContext` / `DbSet<T>` / `SaveChangesAsync` 是**合理且必要**的——它们本就围绕 EF Core 工作，这是自包含用法，不是违规。
- **与持久化/分层无关的组件**（`event-bus`、`object-mapping`、`exception`、`authorization` 等）示例用**中性普通类**演示（如 `OrderNotifier`、`OrderMapping`、`OrderService`），**不要**取名 `OrderAppService` 或强套 `: BaseAppService, IAppService`——那是在“蹭”DDD 概念却又不真正遵守其分层，反而误导读者。
- 需要指引消费者“在 DDD 项目里的正确做法”时，用**一句叙述性 cross-link** 指向 `ddd-struct.md`（例：“在采用 DDD 四层基座的项目里，实体通常继承 `FullAuditedEntity<TKey>`、经仓储读写”），**只作文字说明、不在示例代码里引入该类型**。
- “经仓储 + AppService/DTO 分层”的完整示范，归位到 **`framework/docs/ddd-struct/ddd-struct.md`**（它才引用这些类型）——那里是分层最佳实践的唯一权威出处，组件文档不重复、不承担这一职责。

- **例外只有宿主组合行**：`## 注册` 段里为满足本组件运行前提而写的组合代码，可以调用兄弟组件的注册入口（`Add*` / `Use*` / `Map*`），如"另需宿主注册 `AddUnitOfWork()`"。组合行只表达"宿主还要注册什么"，不使用兄弟组件的其他类型；`## 使用` 等其他段的示例不在例外内，需要兄弟组件的能力时改用本组件闭包里的类型，或改成指向兄弟文档的文字链接。

- **示例的 `using`**：代码块要么不写任何 Leistd 命名空间（默认引入本家族的命名空间），要么完整写出所需的全部 Leistd 命名空间；写了任一 Leistd `using` 的代码块不再默认引入。含 `...` 等占位、无法独立编译的代码块在前一行标注 `<!-- no-compile: 理由 -->`（理由必填）。`framework/build/test-package-consumption.ps1` 按此编译 `## 注册` 与 `## 使用` 段的代码块。

> 一句话判据：**看这个组件的 csproj 引用了什么，示例就只能用什么**（外加原生 .NET）。示例引入了 csproj 里没有的组件类型 = 违规；`## 注册` 段的宿主组合行除外。

---

## 6. 公共 API 的设计与变更

### 6.1 设计

- 名称表达领域语义：类型用名词，方法用动词，接口使用 `I` 前缀，异步方法使用 `Async` 后缀；协议规定的方法名除外。
- 一个名称只表达一个职责；两个字段若没有独立变化和独立消费者，应合并为一个事实源。
- 可空性表达真实缺失状态，不照抄相邻类型；能够稳定推导的值不重复存储。
- 默认值必须可读、可用，并与 Options 验证和运行时行为一致。
- 共享映射与常量放在所有消费者可引用的最低层，派生值不得维护第二份。
- 公共接口优先保持最小；仅一个实现且没有替换需求时，不为形式一致额外抽象。
- 扩展点先说明实际需求：部署参数用 Options，替换实现用窄接口与 TryAdd，HTTP/EF 定制优先采用原生钩子；仅为明确继承需求开放 protected virtual。写明生命周期、缺失状态及不可绕过的租户、身份、撤销或防重放约束，不将所有成员机械设为 virtual。
- **组件不改写官方类型。** 官方类型的形状与语义属于普通宿主的标准行为，组件不认领、不改写（如不把 `HttpValidationProblemDetails` 改成自己的数组形、不覆盖 `HttpContext.TraceIdentifier`）。组件自己产出的类型可以有自己的形状；需要同时消费两者的一方（服务客户端、模板前端）两种都识别。
- **行为差异按类型契约区分，不加选项开关。** 能从类型判断的就按类型判断：`AddDddDbContext<T>()` 只在 `T : BaseDbContext` 时挂 DDD 拦截器，控制库这类普通上下文自然不挂，不需要 `EnableDddInterceptors` 之类的选项。也不要拿"某个服务注册了没有"推断模式——那是代理变量，换个依赖就会推错；确需模式标记时由注册方显式登记标记服务（如锁组件的"本地锁充当分布式锁"标记）。
- **由宿主配置的组件，注册入口只有一种形态：** `AddXxx(Action<TOptions>? configure = null, string configSectionPath = TOptions.SectionName)`。内部先 `AddOptions<TOptions>().BindConfiguration(configSectionPath)`，再应用 `configure`，并挂上 `ValidateOnStart()`；校验消息按实际传入的配置节报键名（§6.2）。
  - 不另设 `IConfiguration` 重载：只传委托的宿主也要拿到配置文件里的值，两个入口并存时总有一个会漏绑定。
  - 没有主机的纯 `ServiceCollection`（测试、工具）由调用方注册 `IConfiguration`。
  - 按名称区分实例的入口（如 `AddServiceClient`、`AddClientCredentials`），默认配置节随实例名变化，无法写成常量：`configSectionPath` 声明为可空、默认 `null`，省略时取 `Leistd:ServiceClients:{Name}`（附加能力再加子节，如 `:TokenExchange`），并在 XML 注释里写明默认路径；显式传入的值仍按非空白校验。
  - 两个已登记的例外不带 `configure`：`AddRemoteTenantConnectionResolution` 只收配置节，`AddHostSettings` 唯一的委托用于声明设置绑定。它们的选项各只有一个调优值（`TenantRouteCacheOptions.CacheLifetime`、`HostSettingOptions.RefreshInterval`），宿主经配置节或 `services.Configure<T>` 调整。不为补齐形态在已有参数中间插入 `configure`：按位置传参的调用会静默错位（可选参数只能追加在末尾）。新增入口不得自行扩列这类例外。
  - 不适合放进配置文件的选项（如签发方决定的 claim 名）不绑定配置节，只走委托。其中含运维可能想按环境调整的值时（如 `HubIdentityOptions.RevalidationInterval`、`LocalTenantConnectionOptions.ControlPlaneConnectionStringName`），在该属性注释里写明不绑定配置的原因和需要调整时的做法；纯代码事实不必逐个说明。
- **组件端点绑定的输入 DTO 必填用 `[Required]`，不用 C# `required`**：引用类型 `= null!`，值类型声明为可空；Minimal API 不自动执行 DataAnnotations，由用例入口 `Validator.ValidateObject` 验证后再取值（不 `GetValueOrDefault()`），集合元素逐条验证。`required` 留给输出与程序调用载荷（如 `NotificationInputDto`），以及"必须出现、可以为 null"的存在性字段（如 `UpsertTenantConnectionInputDto.ExpectedVersion`，缺键在解析阶段拒绝）。查询串里的枚举取值保留 `string` 绑定，按公开名称在端点转换（见操作记录的 `outcome`）。
- 名字归实现它的一方：框架只定义自己实现的名字，并放在拥有它的契约上（如 `INotificationChannel.InAppName`、`NotificationInputDto.DefaultType`）；通知类别、渠道名这类业务取值由消费方定义，框架不预置业务常量清单。
- **组件发出的错误码自带默认译文**：业务异常在 `new BusinessException(code, safeMessage)` 时无条件给码；中英默认文案作为嵌入资源放在发出错误码的包的 Resources 目录（en.json、zh-CN.json，例：`framework/components/authorization/Leistd.Authorization.Core/Resources/`），在该包的 `Add*` 里调 `AddJsonLocalizationResources(typeof(...).Assembly)` 登记。宿主要改文案时在自己的资源里写同名键，登记顺序保证宿主覆盖组件。
  - Core 层错误码和异常只描述语义，XML 注释不写固定 HTTP 状态。组件拥有的非默认 HTTP 语义在组件 Core 包里用 `MapDefaultCode` / `MapDefaultException` 声明，并在组件自己的 `AddXxx` 里经 `services.Configure<GlobalExceptionOptions>(...)` 自动登记——交给宿主逐个调用的话，漏一个不会有编译或启动错误，只会静默回落成 400。登记映射的类型保持 `internal`，默认状态写进组件文档。宿主通过 `MapCode` / `MapException` 覆盖，与调用顺序无关。Core 里的状态码写成 `(int)HttpStatusCode.X`，不为 `StatusCodes` 常量引入 Web 依赖。代价是组件 Core 要依赖 `ExceptionHandling.Core`——多数组件本就为 `BusinessException` 引用它；HTTP 默认状态以 int 表达，Core 仍不依赖 ASP.NET Core 程序集。
  - 新增错误码时只为非默认 HTTP 语义在组件 Core 里登记默认映射（或在宿主映射）并补针对性测试；未映射的 `BusinessException` 故意回落 400，不登记冗余的 400 映射。422 只在协议确有区分价值时显式使用。
  - **协议层失败不发错误码**：输入校验、未预期异常、上游故障等只有状态码语义的失败只返回状态码、本地化标题（`Title:{status}`，译文在 `Leistd.Localization.Core`）与 `traceId`，不合成 `Error:*` 这类与状态码一一对应的码（RFC 9457 §4）。错误码只用于调用方需要据以分支的业务语义。
  - **占位符的名字与基数属于公共契约**：`{Name}` 改成 `{Names}`、或由单值改为多值拼接，都要按破坏性变更处理并写进脚注——宿主的译文是照着占位符写的，改了它等于让宿主的句子渲染错乱（`权限"A, B, C"未定义`）。
  - 宿主**不要**复制组件的译文：一字不差的副本会在组件改文案时把旧文案静默钉死。只写确实要改的那几条。
- 需要可还原的加密时直接用宿主的 Data Protection：注入 `IDataProtectionProvider`（只引用 `Microsoft.AspNetCore.DataProtection.Abstractions`），在构造函数里 `CreateProtector` 一次并复用；用途字符串固定、带命名空间与版本号，改它等于让已存密文全部不可解；要按名称隔离时由同一个保护器 `CreateProtector(名称)` 派生子用途；解密只捕获 `CryptographicException`。不另立加密接口或静态包装——换密钥设施在 Data Protection 这一层换（密钥存储与密钥加密都可替换）。

### 6.2 参数与配置校验

判据来自 FDG（参数不合法抛 `ArgumentException` 系并设 `ParamName`，**对象状态**不对才抛 `InvalidOperationException`）与 ASP.NET Core 自身的选项类（`Validate()` + `ArgumentException.ThrowIfNullOrEmpty`）。五条：

1. **方法参数用 BCL 守卫**：`ArgumentNullException.ThrowIfNull`、`ArgumentException.ThrowIfNullOrWhiteSpace`、`ArgumentOutOfRangeException.ThrowIf*`。不手写 `if + throw` 重复它们已有的判断，**也不自建 `Check` 一类的守卫工具类**——那是 BCL 提供这些方法之前的写法，再包一层只会让参数名要手写。枚举这类没有对应守卫的，手写 `throw new ArgumentOutOfRangeException(nameof(x), x, "…")`。
2. **`Map*` / `Add*` / `Use*` 的选项对象，校验写在选项类自己的 `internal void Validate()` 里**，入口只调 `options.Validate()`。缺必填项抛 `ArgumentException`（`ThrowIfNullOrWhiteSpace` 借 `CallerArgumentExpression` 把 `ParamName` 填成属性名）。**不要每个类再写一个私有的"为空就抛"辅助方法**：同一段逻辑复制到每个组件后，消息格式会各走各的。
3. **走配置绑定的 Options 用 `IValidateOptions<T>` + `ValidateOnStart()`**，验证器单独成文件、与选项类同目录。失败一律 `ValidateOptionsResult.Fail(IEnumerable<string>)`（一条失败一项，运维一次能看全），每条消息以**配置键**开头（`Leistd:Email:Smtp:Host is required.`）；由宿主在代码里配置、没有配置节的选项，改以**类型名.属性名**开头。
4. **异常类型按原因分**：值不合法 → `ArgumentException` 系；宿主没注册、重复注册、组合非法 → `InvalidOperationException`；运行期依赖缺失 → 组件自己的业务异常。同一个条件只在一处校验：启动期已经拒绝的，运行期不再写一遍。
5. **宿主组合错误：能从已登记的事实推断就在启动期校验，推断不了就写组合规范，不加声明式 API。**
   - 可以校验的，判据都来自框架自己的注册：代理工厂登记的标记（`UnitOfWorkWeavingCheck`）、登记了周期任务却没有调度器（`RecurringJobSchedulerCheck`）、开了注册表校验却没注册存储（`TenantStoreRegistrationValidator`）、同一存储被注册两次（`EnsureSingleAuthoritative`）。标记由注册方自己放（见 §6.1），同 ASP.NET Core"端点带授权元数据却没有授权中间件"时报错。
   - 要宿主**先声明意图**才能校验的，不加（如"声明本系统分库、再校验是否装了租户连接路由"）：会漏调注册的宿主同样会漏调声明；把声明与注册放在一起，声明又是多余的。这类错误写进组件文档的组合规范——该注册什么、多宿主时放进共用的组合方法、业务侧需要硬保证时自加守卫——理由同设计原则 §5 第 4、5 条。

配置缺失**不静默兜底**：不 clamp（`Math.Max(1, capacity)` 会把配错的 0 变成 1，日志上看不出来）、不静默跳过。确有"可以不配"的项，在 XML 注释和组件文档里写明它可以不配、以及都不配时在哪里失败。唯一的例外是运行期热更新：新值校验不过时记错误日志并保留上一组有效值，不让一次错误配置把正在跑的实例打挂。

---

### 6.3 变更

- 直接收敛到最终 API，不保留旧成员、桥接包或双配置键；破坏性变化由提交脚注说明变化与必要动作，进入 release notes。开发期不强制维护旧版对照，见[版本规范](./versioning.md#发布说明)。
- 原子更新源码、测试、模板消费者、XML、组件文档和依赖 API 字面量的校验脚本。
- 同时检查签名变化、语义变化，以及删除成员后是否会静默绑定到基类同名成员。
- 公共 API 必须有真实消费者验证；Template 未消费时，使用隔离包消费项目或最小宿主覆盖主路径。
- 示例按契约维护：必须能编译，并与默认值、异常和生命周期语义一致。

### 6.4 带持久化组件的纵向切片

带持久化的组件拥有完整纵向切片，宿主只做组合、覆写与业务词汇。分包同微软 HealthChecks（抽象 / 实现 / `MapHealthChecks`）与 Identity（`Extensions.Identity.Core` / `MapIdentityApi`）：

| 包 | 内容 |
| --- | --- |
| `*.Core` | 契约、用例服务（`I*ManagementService` 一类，实现 `internal`）、DTO、错误码与嵌入的默认译文 |
| `*.EntityFrameworkCore` | 存储实现与数据维护（保留期、归档等周期任务） |
| `*.AspNetCore` | Minimal API 的 `Map*` 扩展，端点只做绑定并调用 Core 用例 |

端点形态：

- `Map*` 扩展挂在宿主给的 `IEndpointRouteBuilder` 上并返回 `RouteGroupBuilder`；路由前缀由宿主 `MapGroup` 决定，组件不写死。
- **授权策略名必填**，由 `Map*` 的选项给出，漏配时映射阶段即抛出。组件不内置默认策略，也**不在路由组上加无参 `RequireAuthorization()`**：那等于把宿主的默认策略叠到每个端点上——机器端点因此被要求自然人身份，而带具名策略的端点会多出一条宿主没在映射处要求过的条件，两者都只在运行期显形。
- "只要求是当前登录用户"的端点（读自己的设置、自己的权限、通知中心）同样要一个具名策略（`AccessPolicy` / `CurrentPolicy`），由宿主把它对"默认主体"的定义显式命名后传进来。
- 组件不替宿主指定认证方案。具名策略省略方案时，由认证结果决定 Challenge 还是 Forbid，再由宿主的默认方案回应；策略自带方案时用该方案。因此宿主须保证默认挑战方案对无凭据请求回 401（例如多方案宿主让挑战与认证走同一个按请求分流的方案），否则只带权限要求的组件端点匿名会被答成 403。
- 端点名带组件前缀，前缀以 `NamePrefix` 常量公开（`Leistd.<家族>.`），宿主据此按名称追加元数据；不使用 .NET 10 已弃用的 `WithOpenApi`。
- 用例有结果（新建或改后的对象）时直接返回 DTO，没有结果的写操作返回 204；分页用 `Leistd.Data` 的 `PageRequest` / `PagedResult<T>`；是否包装 `{code, message, data}` 由宿主在路由组上 `WithResultWrapper()` 决定。
- 组件内的 DTO 投影手写，不引入对象映射依赖。

业务接缝只开**窄钩子**：组件确实依赖宿主模型的地方（主体目录、租户开通与启用前置、设置值校验、收件人解析）声明一个小接口，由宿主实现，组件不引用宿主实体。组件状态变化需要让宿主留痕或联动时，经可选的 `ILocalEventBus` 发本地事件（如 `SettingChangedEvent`、`PermissionGrantsReplacedEvent`、`TenantChangedEvent`），在工作单元内推迟到提交后分发，不为审计另开钩子。

周期任务注册时必填 `RecurringJobScope`，不提供通用的 AOP 锁特性：锁只保效率，正确性靠作业幂等与水位（Kleppmann）。保留期默认值按数据性质定：审计类默认关闭、启用时天数必填（期限受法律合同约束，类库无从知道）；运营类默认开启。

内聚按完整消费能力与依赖边界判断：通用机制归组件，业务实体、权限、配额和锁/事务边界归用例或宿主。优先复用原生契约与扩展点；只为实际替换场景增加窄接口、Options或钩子。不因相似流程抽泛型状态机，也不为单一助手建立实现包、迫使应用层引用传输栈。审视候选必须形成迁入或保留的明确结论，并同步当前调用方与稳定契约。

### 6.5 替换与删除

优先官方机制，但替换的对象是**平行实现**，不是面向业务的抽象。删除或替换一项能力前回答三个问题：

1. **官方能否完全覆盖？** 逐项比语义、作用范围（HTTP 之外的后台任务、消息、外部系统）、替换点。只覆盖默认情况的不算。
2. **成熟框架为何保留？** 查同类成熟框架的当前做法；它们保留的，要说出为什么我们不需要。
3. **没有调用方是不是扩展点？** 接口、`TryAdd` 注册、选项、组件的 `AddXxx` 入口是给宿主和未来实现用的，零引用不是删除理由；删前问"替换者要多写什么"。

三问是删除前的检查，**不是保留的理由**。零引用不能单独证明该删；同样，"是个抽象""将来可能用到""别的框架也保留"也不能单独证明该留。保留要说出本框架支持的具体语义或替换场景（谁会替换、替换时写什么），并掂量维护成本。"官方完全覆盖"针对的是决定继续支持的场景；主动收缩一项能力时，写明是能力删除及其影响，不说成等价替换。

据此分三种做法：

| 情形 | 做法 | 例 |
| --- | --- | --- |
| 官方完全覆盖，封装不带额外语义 | 删除，升级说明写明官方入口 | `MapsterProfile` → Mapster `IRegister`；自建单飞 → `HybridCache` |
| 抽象有官方没有的语义或替换点 | **保留抽象，默认实现取官方值，只删内部的平行实现** | `ICorrelationIdProvider` 默认取 `Activity.TraceId`；资源授权保留业务入口，判定走 `IAuthorizationService` |
| 没有读取方的配置键、死代码、无扩展意义的工具函数 | 删除 | 无人读取的选项字段 |

删除仍在使用的能力时，组件当前文档和升级说明都必须给出**具体的替代入口**（类型、方法或配置键），不能只写"改用官方机制"；死代码不必虚构替代。

这里的升级说明指破坏性提交的 `BREAKING CHANGE:` 脚注及由它生成的 Release 正文（见[版本规范的发布说明](./versioning.md#发布说明)），不要求独立的升级指南文件。

### 6.6 依赖注入

**生命周期**先按状态所有权、并发安全、依赖链与实际消费作用域判断：

| 判据 | 生命周期 |
| --- | --- |
| 持有请求或工作单元状态 | `Scoped` |
| 跨请求共享且线程安全 | 可 `Singleton` |
| 其余 | 默认 `Transient` |

`Singleton` 不得直接或间接捕获 `Scoped`；依赖作用域服务的 `Transient` 必须在正确作用域解析（如周期任务每次执行新建作用域）。

**注册方式**按登记意图区分：

| 意图 | 写法 |
| --- | --- |
| 可替换的单实现 | `TryAdd*`，宿主先注册或之后 `Replace` 均可覆盖 |
| 多实现并存 | `TryAddEnumerable` |
| 按业务键登记 | 入口参数携带键（如 `AddRecurringJob<TJob>` 的任务名、命名客户端名） |
| 按名称区分的配置 | 命名 Options |
| 有意覆盖其他包的默认实现 | `Replace` 或 `Add`，行内注释写明原因（如 `AddSecurity` 用 HTTP 主体访问器替换 Core 默认值） |

注册入口须幂等：相同登记重复调用不重复生效；不同参数重复调用按入口契约处理（合并、覆盖或抛 `InvalidOperationException`），并在 XML 注释写明。注册面的测试要求见 §7.3。

---

## 7. 测试

测试项目的**布局、命名与 csproj** 由 `scripts/check-test-layout.py` 与 `scripts/check-csproj-conventions.py` 强制；
写什么样的用例没有机械判据，按下面的口径。

### 7.1 目录与命名

```text
framework/tests/
├── Directory.Build.props        共享属性与测试包（TargetFramework、打包覆盖、xunit、runner、coverlet）
├── shared/                      非测试项目：Leistd.TestBase（收编的替身与断言词汇）
├── components/<kebab-家族>/     与 framework/components/ 同名同结构
└── ddd-struct/                  与 framework/ddd-struct/ 并列，保住依赖方向的一级划分
```

- 测试项目名为 `Leistd.<真实包前缀>.Tests`；跨家族端到端用例放主家族项目的 `EndToEnd/`；没有独立测试项目的家族在 `check-test-layout.py` 的 `WAIVERS` 写明理由。测试方法名与 `DisplayName` 用下划线分隔的英文句子（`Endpoint_error_throws_ServiceClientException`），背景写进 XML 注释（`check-test-names.py`）；方法名首字母大写。断言说明、失败消息和测试里抛出的异常（含种子、夹具）等开发者诊断消息用英文（`template/scripts/check-test-diagnostics.py --repository`，与模板测试同一判据）；业务词条、预期的本地化内容、中文注释与测试数据不受此限，人工验收的进度日志、报告说明和维护工具（含闸门脚本）的输出也不受此限。csproj 只写 `FrameworkReference`、特有 `PackageReference` 与 `ProjectReference`，共享属性由 `framework/tests/Directory.Build.props` 注入（`check-csproj-conventions.py`）。

### 7.2 项目内部组织

项目根只放用例文件。新文件优先按**被测行为**命名；只测单一类型的单元用例可以按该类型命名（`XxxTests`）。已有文件不为统一命名批量改名。出现下面任一情形才分子目录：

| 情形 | 子目录 |
| --- | --- |
| 用例文件多于约 10 个 | 按**被测包名去掉家族前缀**分：`Core/`、`AspNetCore/`、`EntityFrameworkCore/`、`Memory/`、`Redis/`、`OAuth/`、`Refit/` |
| 存在跨实现的抽象契约套件 | 套件放 `Contracts/`，各实现的派生与专属用例放实现名目录 |
| 本项目专用的测试替身 | `TestDoubles/` |
| 夹具内容文件 | `TestResources/` |

子目录名经 IDE0130 变成命名空间段，同样受 `check-csproj-conventions.py` 的自重复/缩写规则约束。
被测包名与家族名冲突时（`Leistd.ObjectMapping.Tests.Mapster` 里裸写 `Mapster` 会解析到自身）
用完整限定名引入，不要为此改目录名。

### 7.3 写什么样的用例

- **一个行为一个文件**，断言可观察行为而不是实现细节。
- 测试注释解释断言保护的行为，不复述代码或记录修复轮次。
- **分支组合用 `[Theory]` + `[InlineData]`**，不要把同一逻辑复制成多个 `[Fact]`。
- **每个 `DependencyInjection.cs` 至少三条用例**——注册结果与生命周期、重复调用幂等、
  与相邻组件的覆盖/共存关系。用 `Leistd.TestBase.Assertions.ServiceCollectionAssertions`。
  显式组合模型里"注册面正确"就是公共契约，且编译期完全看不出来。
- **同一契约有多个实现时先写抽象契约套件**（`Contracts/` 下的 `abstract class`），
  各实现派生：实现间的语义差异（如零超时）靠人记得"两边都改"挡不住。
- **实体配置、唯一索引、全局查询过滤器必须用关系型 Provider**（SQLite in-memory）。
  EF InMemory 全内存求值，会让被违反的约束和不可翻译的查询静默通过。
  只碰变更跟踪器、不碰 DDL 的测试可以用 InMemory。
- **替身优先用官方实现**：时间用 `FakeTimeProvider`（`Microsoft.Extensions.TimeProvider.Testing`），
  日志用 `FakeLogger`（`Microsoft.Extensions.Diagnostics.Testing`）。手写替身只在官方没有时才写。
- **需要外部服务的用例可以跳过，但跳过的代价必须由 CI 承担。**
  `dotnet test framework/Leistd.Framework.slnx` 必须在没装 Redis / PostgreSQL 的机器上全绿，
  所以这类用例用 `[SkippableFact]` + `Skip.IfNot(可达性, 原因)`，默认连本机端口、可用环境变量覆盖。
  作为交换，**CI 必须显式提供该服务并在跑测试前探活**——服务缺失时那一步直接红，
  而不是让这一批用例悄无声息地跳过去。当前只有 `Leistd.Lock.Tests` 的 Redis 契约走这条路。
- **替身被两个以上项目重复发明就上移到 `Leistd.TestBase`**；语义只是相近的留在各自项目里，
  强行合并会让替身长出一堆只服务某一个调用方的开关。

### 7.4 不追求的覆盖

**覆盖率是体检指标，不是目标。** 下面几类零覆盖是合理的，不必也不应该为它们写用例：

| 类别 | 例 | 为什么不写 |
| --- | --- | --- |
| 诊断字符串 | `ToString()` 重写 | 字符串拼接，无分支。断言它等于把实现抄一遍 |
| 单行转发 | `GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync()` | 被测的是框架自己的实现 |
| 空实现 | `StopAsync() => Task.CompletedTask` | 没有可失败的行为 |
| 需要外部服务的私有细节 | Redis 键前缀拼接 | 没有真实服务就只能造假，造假证明不了它 |
| 纯声明 | 接口、DTO、标记特性、常量 | 无可执行代码 |

反过来，**下面几类即使只有一两行也必须写**：

- **注册面**：`AddXxx()` / `MapXxx()` / `UseXxx()`。显式组合模型里它就是契约。
- **安全语义**：端点是否要求登录、权限归属判定、主体解析。漏掉的表现是越权而不是报错。
- **失败与放弃路径**：保存失败后丢弃已收集的领域事件、子工作单元不得提交、取消令牌是否真的传下去。
  这些只在出事时才执行，没有用例就等于从未运行过。
- **同一逻辑的第二个入口**：同步/异步保存、配置绑定/委托两个重载。
  只覆盖一条时，另一条改坏不会红。
- **可翻译性**：谓词是否真被翻译成 SQL、唯一索引是否真进了 DDL。
  这类必须用关系型 Provider，InMemory 会让它们静默通过。

优先覆盖静默错误，同时验证公共契约规定的异常、取消与拒绝行为。

**覆盖率不设门禁，CI 也不收集**——设了固定 fail-under 就会诱导为数字补测试，
而上面这张表里最该补的那几类恰恰不是靠百分比找出来的。需要体检时本地跑：

```bash
dotnet test framework/Leistd.Framework.slnx -c Release \
  --settings framework/build/coverage.runsettings --collect:"XPlat Code Coverage"
```

分工要说清：`scripts/check-test-layout.py` 保证到**家族**这一级——家族有测试项目、
项目名对得上、已登记进解决方案。**它证明不了家族内每个发布包都被加载**，
那要么读覆盖率、要么反射公共 API 做对账，都是人工体检，不做机械门禁。

### 7.5 不要引入的模式

单元测试只组合所需服务，不在每个测试类构造中启动整个应用。

- 组件与 DDD 基座里新增程序集扫描（`GetTypes()`、`DefinedTypes`、`Assembly.Load`）必须有明确理由，
  且不得进入 `AddXxx()` 的公共路径——只能在调用方显式要求扫描时发生。
- 需要真实宿主的用例（`TestHost` / `WebApplicationFactory`）用 `IClassFixture` 或 collection fixture 共享。
  用例之间有逐测可变的宿主状态时（每条用例的服务注册、选项或中间件组合不同，或用例会改写宿主内的单例状态），可以每条新建宿主，并在用完后释放；只是为了省事而每条新建不在此列。

## 8. 提交前自检

按变更范围选择验证，配方见[质量规范的本地验证配方](./quality-assurance.md#本地验证配方)，测试项目的选择与执行命令见[§框架与真实依赖](./quality-assurance.md#框架与真实依赖)；本文件不复述。

静态闸门清单只以 `pwsh scripts/check-all.ps1 -List` 的输出为准，CI 也只调它一处；新增闸门加进该脚本，本文件不跟着列。
需要构建产物或运行环境的验证（矩阵、PostgreSQL E2E、包消费）不在其中，各有自己的入口。

交付不留待办标记，规则见[设计原则 §4](../architecture/design-principles.md#4-验证原则)。

本地 NuGet 包统一输出到仓库根 `.tmp/local-feed`，不要临时发明其它产物目录；CI 发布产物仍使用 `framework/artifacts`。包消费检查会验证 DLL、XML、随包文档和依赖闭包，并在 `.tmp/package-consumer/` 使用隔离 NuGet 配置构建最小消费项目；本地可用 `-PackageIds Leistd.Xxx` 只检查受影响包。新增第三方包时确认已在 `framework/Directory.Packages.props` 登记；新增包发布前确认 `PackageId` 唯一。

## 9. 脚本与命令的跨平台约定

框架面向 Mac / Linux / Windows 三平台开发者，构建与工具命令必须可移植：

- **能用 `dotnet` CLI 直接表达的，不要包一层脚本**。`dotnet build/pack/nuget push` 三平台命令完全一致、零额外依赖——文档与 CI 直接写 `dotnet …`，不写 `pwsh xxx.ps1` 去包装它。
- **确需脚本的复合逻辑**（如文档校验 `framework/build/check-docs-*.ps1`）用 PowerShell 7（`pwsh`，本身跨平台），并遵守：
  - 可直接执行的入口脚本首行 `#!/usr/bin/env pwsh`（注释帮助块 `<# … #>` 与 `param(` 放在它之后，帮助块与 shebang 之间空一行，否则 `Get-Help` 认不出帮助块）；只被其他脚本点源加载的库脚本不要求（如 `scripts/template-matrix-scenarios.ps1`、`scripts/quality-validation-plan.ps1`）；
  - 路径分隔符用 `[\\/]` 正则或 `Join-Path`/`[System.IO.Path]`，不硬编码 `\`；
  - 不用 Windows 专属 cmdlet（`Get-WmiObject` 等）或调用 `cmd`/`*.exe`。
- **纯文本分析的静态闸门**（`scripts/check-*.py`）可用 Python 3.10 及以上（同样跨平台），本地与 CI 均以它为前置。
  调用入口负责探测解释器（`python3` 优先，回落 `python` 并校验版本不低于 3.10，找不到则报明确错误），
  不硬编码 `python3` 可执行名——Windows 上通常只有 `python`。闸门以默认编码警告即错误运行，因此要求 3.10。
- **文档命令示例**优先给三平台通用形态；引用的脚本必须真实存在（不要写引用尚未创建的脚本的命令）。
