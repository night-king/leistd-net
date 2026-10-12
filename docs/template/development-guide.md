# 模板开发规范

## 1. 定位

`template/` 是可发布的 `dotnet new` 项目模板，也是 Leistd.* NuGet 包的参考消费端。模板维护需要同时保证源模板正确、条件裁剪正确以及生成结果可构建运行。

## 2. 依赖方向

- `Domain` 保存业务模型和内层抽象，不依赖 Application、Infrastructure 或 Api。
- `Application` 编排用例并依赖 Domain，不依赖 Infrastructure。
- `Infrastructure` 实现持久化和外部适配器并依赖 Domain；需要实现 Application 抽象时可依赖单独的 Contracts 项目，当前模板未拆该项目。
- `Api` 是组合根，可同时引用 Application 和 Infrastructure，并负责服务注册与端点映射。
- 模板只通过 `PackageReference` 消费框架包，本地联调也使用 `.tmp/local-feed`。

## 3. 条件生成

修改条件化功能前先读 `template/.template.config/template.json`。条件代码、文件排除、项目引用、DI、前端路由、Mock 和文档必须作为一个场景整体调整，生成后不得残留 `<!--#if`、`<!--#endif` 或模板占位符。

### 3.1 加开关的两条规则

**规则一：数据类型只问一个问题——同一产物能否同时要这两者？**

| 答案 | 类型 |
| --- | --- |
| 不能，且互斥取值 ≥3 个 | 枚举 |
| 其余一律 | **布尔 `IncludeXxx`** |

服务形态使用互斥枚举 `ServiceRole`：

| 取值 | 含义 |
| --- | --- |
| `Identity`（默认） | 本地口令身份与交互认证，签发 OIDC 令牌 |
| `Standalone` | 本地口令身份与 Cookie 会话，不带授权服务器 |
| `Resource` | 验证远端令牌，保留本地用户投影、角色与权限事实 |

能力按可独立取舍的产品边界划分；实体审计、软删除、安全失败计数、锁定与撤销属于基础行为，不作为公开参数。

| 布尔参数 | 默认 | 生效范围 |
| --- | --- | --- |
| `IncludeFrontend` | true | Resource；本地交互认证始终保留前端 |
| `IncludeMultiTenancy` | true | 全部角色 |
| `IncludeRealTime` | false | 全部角色，独立控制业务实时能力 |
| `IncludeEmail` | true | 本地口令身份 |
| `IncludeOperationRecords` | true | 全部角色，控制内置历史产品；关闭后保留安全行为日志 |
| `IncludeNotifications` | false | 全部角色，独立于业务实时能力 |
| `IncludeExternalLogin` | false | 本地口令身份 |
| `IncludeLocalization` | false | 全部角色，前端载荷还要求有效前端存在 |

交付参数 `Ci`（choice：`github` 默认、`gitlab`、`none`）只选择 CI 薄壳文件，不是产品能力：不进入有效形态与两两组合，`template/scripts/verify.ps1` 总是生成。薄壳只准备环境并调用 `verify`，验证步骤只在该脚本维护。

Framework 提供通用契约与适配器，不感知角色或模板参数。Template 在组合根选择能力并裁剪用例、依赖、迁移、界面、Mock、测试、配置及文档。六个后端项目名称保持 Api、Application、Domain、Infrastructure、DbMigrator、Client；服务职责通过项目名前缀表达。

### 3.2 有效能力集中派生

`template.json` 是参数与有效能力的唯一来源，不使用 `isEnabled`；角色专用参数仅在有效能力表达式中直接消费。

```text
LocalIdentity = ServiceRole != Resource
OpenIddictServer = ServiceRole == Identity
RemoteTokenAuth = ServiceRole == Resource
ExternalLogin = LocalIdentity && IncludeExternalLogin
SpaFrontend = LocalIdentity || IncludeFrontend
ResourceBrowserSession = RemoteTokenAuth && IncludeFrontend
Email = LocalIdentity && IncludeEmail
Impersonation = LocalIdentity && IncludeMultiTenancy && IncludeOperationRecords
```

条件按能力含义书写：`LocalIdentity` 表示本地口令身份，不能解释为“存在用户表”；Resource 同样持有用户投影和本地授权。单租户仍保留消费者需要的 MultiTenancy Core 和宿主数据库执行能力，业务 `TenantId=null` 及部分唯一索引保持统一。

通知与实时四种组合分别为无 Hub、通知 Hub、业务实时 Hub、合并实时 Hub；合并形态只建立一条前端连接。邮件关闭同时裁剪完整发送和验证资产，联系信息字段按实际消费者保留。历史关闭同时裁剪数据库与读侧产品，成功安全日志在真实事务提交后输出；日志模式有提交后进程退出导致丢失的窗口，不等同于数据库同事务保证。

Resource 的首位管理员通过正式 DbMigrator `--grant-admin <sub> [--tenant <id>]` 引导，默认只读，`--apply` 才写入；不依赖人工 SQL 或首次访问自动授权。授权载体是 Admin 角色成员关系（本地无此用户时先建最小主体行），不写用户级直接授予；普通引导不推翻之后的停用、删除与移出 Admin。初始化锁由命令持有到提交之后。生成项目的具体命令说明放在其后端 README。

管理员不能停用自己（`User.CanBeDisabledBy`），本地身份另不能删除自己（`CanBeDeletedBy`），拒绝都在任何副作用之前；Resource 本地没有删除用户的端点，这条规则不为它新增删除能力。自我降级不拦，由部署级恢复兜底，不做运行期的"最后一位管理员"预测。Resource 在同一命令上加 `--restore`；多租户本地身份用 `--restore-tenant-admin <tenant-id>` 恢复开通时建的原 `admin` 记录（宿主有内置超管，不需要）。两者都在一个工作单元里撤回删除、启用、只补回 Admin 成员关系，并经 `IPermissionGrantManager` 把当前侧别可用、Admin 角色缺少的权限补授给这个共享角色（`IPermissionGrantSeeder` 只在授权版本为 0 时播种，补不回撤过的授予），预演不写、无缺口不写。目标不存在时两形态不同：租户恢复找不到原 `admin` 记录就失败，不新建账号；Resource 恢复在本地没有该主体时按正规引导建最小投影行，不创建远端身份或凭据。两条命令的服务组合都不含 `AddInfrastructureServices` 与 `AddApplicationServices`，只组合存储、锁与 `AddApplicationDefinitions`（权限与操作动作定义），租户恢复另加密钥环，Resource 引导另加用户与角色的领域服务和系统初始化器（首次播种）；发信等适配器的启动期校验不挡部署作业，只在 API 注册的能力（如实时推送的发布器）也不进组合；两条命令的容器与 API 一样在生产以外开启 `ValidateScopes` / `ValidateOnBuild`。

### 3.3 模板引擎限制

| 限制 | 失败形态 |
| --- | --- |
| **不支持 `#error` 指令** | 处理该文件时抛 `Object reference not set`，**所有场景生成失败** |
| **`isEnabled` / computed `value` 引用已删除的符号** | 该符号单独用正常，**一进复合条件就 NRE**；报错只有文件名 |
| **注释里写条件指令的字面形式** | 引擎支持 `//#if`，于是把注释当成真实指令，配对错位、**整段代码被静默吞掉**，产物少几百行却"生成成功" |
| **删条件块后留下恒真嵌套**（`#if (X)` 套在 `#if (X)` 里） | 内层永真、`#else` 分支永不可达；也可能触发上一条 |
| **只查 `.cs`/`.ts` 会漏 `.csproj`** | 包引用没剪掉，产物仍带着该依赖 |
| **`isEnabled` 引用 computed 符号** | `String 'X' was not recognized as a valid Boolean`；只接受 parameter |

修改参数结构后先运行 `pwsh scripts/check-all.ps1`，再按[质量规范](../framework/quality-assurance.md#分层执行与时间预算)运行场景矩阵。模板相关的静态检查是 `check-template-symbols.ps1`（悬空符号、注释里的指令字面形式、恒真嵌套）、`check-using-guards.py`（在全部符号取值组合下检查守卫与 csproj 良构）和 `check-async-boundaries.py`（动态连接路径上的同步阻塞），判据见各脚本文件头；场景矩阵只编译登记的场景。

### 3.4 前端条件块：`//#if` 紧贴上一单元，空行留在块内首行

**独立语法单元**（函数、interface、方法、`it(...)` 用例）被条件包裹时：`//#if` 紧跟上一个单元、之间不留空行；块内第一行留空；`//#endif` 紧跟块内最后一行，之后再空一行接下一个单元。

```ts
}
//#if (Symbol)

function conditionalThing() {
  // ...
}
//#endif

export const next = 1;
```

开启时得到「上一单元 + 空行 + 本单元 + 空行 + 下一单元」，关闭时整块消失后剩「上一单元 + 空行 + 下一单元」——两种形态各恰好一个空行。

把空行放在标记外侧（`}` 空行 `//#if`）则关闭时会剩下双空行，prettier 判 lint 失败；而只跑其中一种场景发现不了。**条件块的格式必须按开、关两个场景分别验证。**

这条只管独立语法单元。**数组元素、对象成员、`import` 具名列表**里的条件项本来就不带空行，直接紧贴前后项即可；而当两个场景对同一处的期望本身不同（例如列表变短后 prettier 要求折成单行），就不要在结构内部放 `//#if`，改成两个分支各写一份完整的语法单元。

### 3.5 标准场景矩阵

场景、档位、分片及形态断言唯一维护在 `scripts/template-matrix-scenarios.ps1`。保留原有回归场景，同时覆盖最小本地身份、Resource 纯 API、单租户、日志模式、邮件关闭及通知/实时交互。PR 档覆盖全部可达条件行和有效能力两两取值；高阶交互由关键场景与真实端到端补齐，full 档执行全部登记场景。

登记 `Verify` 的场景（有/无前端、开/关本地化、三种 `Ci`）在完整阶段直接运行生成项目的 `verify.ps1` 完成构建与测试，矩阵补运行时冒烟、spec 发现范围与产物断言；其余场景与按前后端拆分的阶段由矩阵逐步执行，因为 `verify` 不提供子集开关。每个场景都核对 `verify -List` 与矩阵阶段一致，两处步骤不会漂移。

能力参数原始输入共 768 组，有效形态共 320 组。`test-template-generation.py` 以默认 `Ci` 实际生成全部有效形态，另以有无前端两类形态核对 `Ci` 三种取值只改变 CI 文件集合及描述它们的文档，并在每种 `verify.ps1` 产物上核对步骤清单与失败即停；检查资产、JSON、项目结构、迁移与依赖不变量，并以实际生成结果验证角色无效参数的代表性内容等价。随机 UserSecretsId 是唯一排除的非确定性字段。轻量生成不能替代矩阵编译、数据库运行或浏览器验证。

生成检查还验证 TypeScript 相对模块与组件模板/样式资产闭包，防止文件已裁掉但调用方仍引用。词条源码可用模板条件指令包住可选键；源码检查只识别这些指令并校验完整词条集合，不接受普通 JSON 注释或非法内容。全部有效形态的实际词条产物必须是严格 JSON，en/zh 键和占位符逐形态一致；条件结构与符号由独立源码闸门验证。

Standalone 场景专门检验条件独立性：`identity` 与 `resource` 里 `LocalIdentity` 和 `OpenIddictServer` 恰好同真同假，只有 Standalone 形态把两者分开（PR 档由 `standalone-external-login` 承担）。新增能力时不得只验证 `identity` 和 `resource`。

场景断言分两类，缺一不可：`Present`/`Absent` 与 `RequiredTokens` 证明"留下的是对的那一份"，`ForbiddenTokens` 证明"不该留的没留下"。只查缺失会漏掉"两份都在"的情形。

`ForbiddenTokens` **不得为了让场景通过而放宽**——它抓到的每一次都是真残留。若某个词在注释里合法出现，改注释措辞，不改断言。

### 3.6 跨 `ServiceRole` 的 HTTP 契约由框架统一

租户连接的机器端点（`runtime/{tenantId}`、`migration`）与资源服务回源用的远端存储都由多租户组件提供：
Identity 形态用 `MapTenantConnections` 映射端点，Resource 形态用 `Leistd.MultiTenancy.ServiceClient` 的 `AddRemoteTenantConnectionStore` 回源，
两边共用 `Leistd.MultiTenancy.Core` 里的线上 DTO。模板只决定前缀：Identity 在 `ComponentEndpoints` 里的路由组前缀与 Resource 的
`Leistd:ServiceClients:Identity:RoutePrefix`（默认 `/api/v1/tenant-connections`）必须一致。改前缀时两处一起改，并在 Identity 与 Resource 的 HTTP 组合验证中确认。
模板不再手写这组端点的控制器、Refit 接口或 `IMyProjectClient` 方法。

> 这些是 leistd-net 的维护事实，**不要写进 `template/` 源码注释**（见 `developing-leistd-template` skill 的「对外分发边界」）。模板注释只写生成项目自身运行与持续开发需要的知识。

### 3.7 错误码不随本地化裁剪

`BusinessException` 在构造时必填错误码，模板不用 `#if (IncludeLocalization)` 裁掉业务码：多语言形态用它查词条，非多语言形态仍用它做客户端分支和日志聚合，两种形态的机器契约一致。错误码的命名、放置与 HTTP 映射规则见生成项目 [API 规范](../../template/docs/standards/api.md#4-异常与-http-映射)；`*ErrorCodes` 检查验证唯一性、格式和资源键。

## 4. Skill 与规范

- Skill、生成项目规范与入口文件的分工以 [三层交付与 AI 协作](../architecture/collaboration-scenarios.md#2-skill-边界) 为准。
- `template/docs/README.md` 是生成项目唯一文档索引，含按任务读取表；改动 `template/docs/` 时同步该表，核心规范以约 10,000 字符为精简提示值，超出先删重复与冗长示例，再按独立任务主题拆分。调整规范结构或篇幅时运行 `python3 scripts/measure-template-read-cost.py --check`：它按读取表统计五类典型任务去重后的读取字符数（含项目 Skill 的 `SKILL.md` 与所需 reference），超过登记的上限即失败；有意放宽时连同理由修改上限。
- `template/docs/standards/` 只保存工程事实，不重复 Skill 流程；不携带固定需求、规范或报告模板，不预建按需目录。
- 修改任何 Skill 时使用官方 `skill-creator` 并运行 `scripts/validate-skills.ps1`。
- 前端 UI 走 Spartan UI：选型依据见 [`docs/architecture/frontend-ui-library.md`](../architecture/frontend-ui-library.md)，组件用法见生成项目 [前端界面规范](../../template/docs/standards/frontend-ui.md)；确认组件 API 按「`spartan` skill → 本地 `libs/ui` 源码与锁定版本 → 匹配版本的官方文档」，不臆造 Helm/Brain API。`@spartan-ng/mcp` 是仓库维护者的可选工具（根 `.mcp.json`），模板不内置。
- 生成项目 [Spartan 维护约定](../../template/docs/standards/frontend-spartan.md) 的定制登记表由 `scripts/check-spartan-customizations.mjs` 核对，边界是组件集合：用锁定版本的 Spartan CLI 还原上游后，与上游有差异的组件集合必须等于登记表列出的组件集合；它不核对行内改动描述，改 helm 组件或升级时人工逐项核对描述与源码一致。

## 5. 本地框架联调

先在仓库根目录打包，再由模板矩阵通过一次性 NuGet 配置消费：

```powershell
pwsh framework/build/pack-local-feed.ps1
pwsh scripts/test-template-matrix.ps1 -SkipPack
```

需要人工观看前端测试运行时，改用有头 Chromium（CI 仍默认无头的 `chromiumHeadless`）：

```powershell
pwsh scripts/test-template-matrix.ps1 -SkipPack -FrontendBrowser chromium
```

不在仓库 `NuGet.Config` 或生成项目中固化本地源。

## 6. 数据库初始化

- 模板携带可审查的 EF Core 基线迁移；API 启动不执行 `MigrateAsync` 或 `EnsureCreatedAsync`。
- 每个服务的 `DbMigrator` 是一次性部署进程，先于 API 运行，使用 DDL 身份；API 只使用 DML 身份。
- 每个服务在所有物理数据库中使用自己的固定 schema 和迁移历史表。Identity 的租户/OIDC Control DbContext 固定连宿主 Control DB，不跟随租户路由。
- `ConnectionStrings:MigrationTarget` 只用于首次预迁移一个尚未登记的 Dedicated 物理目标；常规发布仍从 Identity 枚举已登记目标并去重迁移。
- 修改迁移策略时必须同步 DbMigrator、基线 migration、生成项目 README、部署说明与真实 PostgreSQL 闭环断言。

## 7. 事务边界

生成项目的工作单元与写后返回规则见 [后端开发规范 §3.6](../../template/docs/standards/coding-backend.md#36-事务与工作单元)。模板内写明了取舍的事务边界不要按通用规则改写，例如 `RoleAppService.DeleteAsync` 在一个工作单元里提交删角色、清关联、清授权与成功记录，原因与失败形态见其 `<remarks>`。

## 8. 宿主与租户侧别

权限侧别、服务内补校验与实体租户维度的规则见生成项目 [认证与授权](../../template/docs/standards/auth.md#权限侧别与租户维度)。侧别是 `AddPermission` 的必填参数，由编译器保证每条权限都已决定侧别，不另建对账测试。

## 9. 测试与开发只用 PostgreSQL（为什么不用 InMemory 或 SQLite）

生成项目只有 Npgsql 一个提供程序：开发连接 `template/deploy/docker-compose.dev.yml` 的本机库，集成测试的建库方式见生成项目 [`testing.md`](../../template/docs/standards/testing.md) §2.1。

1. **InMemory 让生产代码迁就测试**：没有事务、不强制唯一约束、不支持 `ExecuteUpdate`/`ExecuteDelete`，EF Core 官方也不建议用它测试。
2. **SQLite 与工作单元的独立事务冲突**：框架与模板大量使用 `Begin(requiresNew: true)`，SQLite 每个库只有一个写者，外层未提交写入后再开独立事务会等锁超时；它也没有 schema、执行不了 Npgsql 迁移，`decimal`/`DateTimeOffset` 的排序与比较受限。
3. **代价**：集成测试比 InMemory 慢，换来与生产一致的约束、翻译、事务和迁移；单元测试不连库。

分工：集成测试覆盖单个服务在真实库上的全部行为；`scripts/test-template-postgresql-e2e.ps1` 覆盖必须跨进程、跨库验证的部分——`DbMigrator` 命令行与预演、控制库与业务库拆到不同实例、共享与专属租户库的物理隔离、跨进程共用的 Data Protection 密钥环。专属租户库的路由可以在集成测试里另克隆一个库来验证，但不为此在生产组合根里加只服务于测试的分支。

## 10. 验证

按改动选择档位与入口见[质量检查与验证分工](../framework/quality-assurance.md#分层执行与时间预算)的 L1 表；测试分层、端到端分工与 lint 缓存口径见[模板质量验证](./quality-assurance.md)。检查删除/替换必须逐项完成接替与变异验收。

`test-template-postgresql-e2e.ps1` 在真实 PostgreSQL 上验证本地 Framework 包→Identity/Resource 生成→DbMigrator→API→Shared/Dedicated 隔离的整条链路，要求本机 Docker 与 PowerShell（psql 用容器自带的）。矩阵与该脚本默认各自打包当前 Framework 源码。

每次运行使用独立的 run 目录 `.tmp/runs/<run-id>/`（`<run-id>` = PID+时间戳），其下含 `generated-template/`、`local-feed/`、`template-hive/`、`nuget-cache/` 与一次性 NuGet 配置——生成物、包源和 `globalPackagesFolder` 都不跨 run 写入，因此**多个 AI/终端可并行执行**。不得共享解包目录后再“定点清理 Leistd.*”：本地包会在版本号不变时重新 pack，清理会在另一个并发 build 期间抽走 DLL。NuGet 自身的 HTTP 缓存仍会避免重复下载。启动时只清理超过 2 小时未活动且非当前 run 的旧目录（据 `.run.lock` 判活），绝不删正在运行的 run。CI 发布目录仍使用 `framework/artifacts`。

重复调试同一份已 pack 的本地包时可用 `-SkipPack`（读取共享的 `.tmp/local-feed`）；Framework 包内容变化后必须重新 pack，不得让旧的同版本包掩盖源码改动。

## 11. 删除与精简

模板里的删除遵循框架规范 §6.5 的三问，另加两条模板特有的判断：

- **兼容要么完整，要么不留。** 模板只兼容它明确支持的来源和配置组合，并且要完整覆盖该来源产生的全部形状（如响应信封的成功与失败两侧）。只兼容一半的按删除处理，在文档写明启用该来源时要做的适配——半套兼容会让人误以为它被支持。
- **没有读取方的配置键和字段直接删。** 它们承诺了不存在的能力，比缺一个扩展点更误导人。示范性质的通用代码（工具函数、样例端点）若与项目无关或已有官方等价物（Angular 管道、`Intl`），也删；与业务开发者常用能力相关的，保留并至少有一处真实调用。
- **不留待办。** 规则与闸门见[设计原则 §4](../architecture/design-principles.md#4-验证原则)；模板里留下的待办会原样复制进每个派生项目。模板中豁免的上游生成代码是 `frontend/libs/ui`。
