# 模板部署配置只写差异

状态：已完成 · 风险档：中（模板配置与规范，影响所有生成项目的部署样例） · 关联：https://github.com/zengqinglei/leistd-net/pull/167

## 1. 需求

### 问题与背景

CRM V2 三仓（crm、identity、foundation）在 2026-10-24 做了两轮配置清理，删掉了部署样例、Compose、测试夹具和 appsettings 里取值等于"删掉这一行后的生效值"的显式配置，共 107 项。这类配置是噪音，而且代码默认值以后调整时，它们会把旧值悄悄钉住，造成环境漂移。

清理时发现，其中很多项注明"照 template"，源头在模板：
- 模板的 `appsettings.json`、`deploy/docker-compose.yml`、`.env.example` 带着同值配置；
- 模板 `appsettings.json` 的路由缓存一节，注释甚至写着"写出来便于按环境调整"。

这些项不从模板删掉，业务项目下次同步模板时会被重新引入。

框架会话的审视结论见 `/Users/quincy/orca/workspaces/.crm-v2-adaptation/foundation-integration-20261023/config-defaults-framework-review.md`，用户 2026-10-24 裁决其中 4 项全部执行。

### 目标

1. 删除模板里与生效值相同的配置（下表 T1–T13）。
2. 把"部署配置只写差异"的最终规则写进模板规范 `template/docs/standards/settings.md`，成为生成项目的长期约定。
3. 新增一条集成测试，守住 Serilog 最小级别这个必须保留的键。
4. 修正框架里一处过时注释。

### 非目标

- 不改业务三仓。crm Worker 的 `RetentionDays` 基线隐患由业务侧自行确认。
- 不改框架组件的默认值或行为，不新增"配置同值检测"门禁。
- 不改 `AllowedHosts` 等 ASP.NET 官方模板约定。

### 功能要求：删除项

删除时，被删项旁有用的说明迁到对应 Options 属性的 XML 注释，不能丢。"保留"列的项不得删除。

| # | 位置 | 删除 | 保留 |
| --- | --- | --- | --- |
| T1 | `Api/appsettings.json` 的 `SessionCookie` 节（`#if (SpaFrontend)`） | 整节 | — |
| T2 | 同文件 `OAuth`（`#if (OpenIddictServer)`） | `UseDevelopmentCertificates`、`DisableHttpsRequirement`、`AccessTokenLifetime` | `Resource`（跨服务契约值）、`ApiResources` |
| T3 | 同文件 `DefaultAdmin` | `Username`、`Email` | `Password: ""`（必填占位）、`DisplayName`（与代码默认 null 不同） |
| T4 | 同文件 `UserRegistration` | 整节 | — |
| T5 | 同文件 `ExternalAuth` | 整节 | — |
| T6 | 同文件 `Leistd:OperationRecords:Retention` | `Enabled`、`DailyRunHourUtc`、`BatchSize` | `RetentionDays: 365` 及注释（代码默认为空、启用时必填的基线） |
| T7 | 同文件 `Leistd:Notifications:Retention` | `Retention` 子节 | `Email:PublicBaseUrl`（部署决策点占位） |
| T8 | 同文件 `Leistd:MultiTenancy:Routing` | 整节 | — |
| T9 | 同文件 `Leistd:Settings:Hosting` | 整节 | — |
| T10 | `deploy/docker-compose.yml` 的 API 服务 | `Leistd__ServiceClients__Identity__Scope=tenant-routing.read`、`Leistd__MultiTenancy__Routing__CacheLifetime=...`，两行连同其注释 | 迁移作业服务的 Scope 行（DbMigrator 没有基础档）；"运行时身份只查路由"的说明并入 `appsettings.json` 的 `ServiceClients:Identity` |
| T11 | 同文件 | `DefaultAdmin__Username`、`__Email`、`__DisplayName` | `DefaultAdmin__Password` |
| T12 | 同文件 | `Serilog__MinimumLevel__*` 三行及 `# Logging (optional)` 注释 | — |
| T13 | `deploy/.env.example` | `TENANT_ROUTE_CACHE_TTL` 两行 | — |

每一项删除的依据都是与代码默认值或下层叠加结果同值。对照来源：
- 模板 Options 字段初值：`SessionCookieOptions`、`OAuthServerOptions`、`DefaultAdminOptions`、`UserRegistrationOptions`、`ExternalAuthOptions`；
- 框架 Options 字段初值：`OperationRecordRetentionOptions`、`NotificationRetentionOptions`、`TenantRouteCacheOptions`、`HostSettingOptions`；
- 宿主设置绑定的兜底值：`BindOption` 取 Options 默认值，外部登录的 `Bind` 兜底为 `"false"`。

两处顺带调整：
- `.env.example` 中明文写着 `GITHUB_LOGIN_ENABLED=false`、`GOOGLE_LOGIN_ENABLED=false` 的两行，改成与 `SMTP_ENABLE_SSL` 相同的注释写法（`# 可选……缺省 false`）；
- `appsettings.Production.json` 只有 `{}`，保留不动。

### 功能要求：规范

`template/docs/standards/settings.md` 新增一节"部署配置只写差异"，作为 §1 的子节（`### 部署配置只写差异`），放在 §1 末尾。不另起 `## 5`：§3、§4 位于 `SpaFrontend` 条件块内，`!SpaFrontend` 场景会出现 §2 之后直接是 §5 的断号；也不重排编号，以免现有锚点失效。规则内容取审视结论 §1 的最终版，按模板规范的篇幅要求写短：

- **判定口径**：默认值指删掉这一行后**本进程**实际得到的值，按代码默认值 → 基础档 → 环境档 → Compose/env/测试宿主逐层比较，每个进程各自独立。
- **保留情形**：
  - 存在性守卫；
  - 库直接订阅的键（Serilog `MinimumLevel`）；
  - 集合与字典（按绑定语义核对，同值的标量叶子可删）；
  - 环境能力声明；
  - 注释声明有意显式的项；
  - 必填、地址、凭据；
  - 测试依赖的值；
  - 跨服务契约值（只留在一层）；
  - 代码默认为空、启用时必填的基线（每个消费进程都要有）；
  - 部署决策点占位。
- **删除约束**：整行删除，不置空。空字符串、`null` 都不等于缺失：它们会挡掉下层的值，之后按类型和消费路径，可能回落到属性初值、绑定失败或得到 null，以实际类型和消费路径为准。说明迁到 Options 的 XML 注释；删空的节整节删除。
- **集合与字典**：不按整个对象同值机械删除，要按绑定和合并语义核对。字典里与下层同值的标量叶子可以删（如 T12 的 Serilog Override 两项）。
- **有意显式**：注释必须写出为什么要钉住，"方便调整"不算理由。

### 功能要求：测试

新增集成测试，放在 `SettingsContractTests`。宿主只用模板自带的 appsettings 启动，不注入任何 `Serilog:MinimumLevel*` 键。断言以下三点：
- 写入设置 `Logging.MinimumLevel=Debug` 后，生效级别为 Debug；
- 清除设置后，回到基础档的值；
- 删掉基础档的 `Serilog:MinimumLevel:Default` 后，这条测试失败。用变异验证一次，不提交变异。

### 功能要求：框架注释

`framework/components/multi-tenancy/Leistd.MultiTenancy.Core/Leistd.MultiTenancy.Core.csproj:12` 注释里的"TenantRouting 配置节"改为 `Leistd:MultiTenancy:Routing`。

### 验收标准

| # | Given / When / Then | 覆盖 |
| --- | --- | --- |
| V1 | Given 删除 T1–T13 后的模板，且部署**没有使用被移除的自定义变量**，When 生成受影响场景，Then 每个被删键的生效值与删除前相同（按进程区分基础档、Development 覆盖与 Compose；T13 并入 T10 的实际 TTL 验证） | 生效值比对：删除前后各生成一次，读取绑定后的 Options 值或设置默认值，逐项对比；方式由实施方选择，记录命令与结果 |
| V2 | Given 条件裁剪后的 appsettings 与 compose，Then JSON 合法、无多余或缺失的逗号，compose 可解析 | 按选择器给出的场景 `-GenerateOnly`；`docker compose config` 解析生成的 compose |
| V3 | Given 新测试，When 基础档保留 `Serilog:MinimumLevel:Default`，Then 通过；When 删掉它（变异），Then 失败 | 集成测试 + 一次变异 |
| V4 | Given 设置页，When 读取会话时长、注册策略、外部登录、保留期的默认值，Then 与删除前相同 | 现有 `SettingsContractTests` 等集成测试通过 |
| V5 | Given 规范新增内容，Then 读取成本闸门、文档引用与锚点闸门通过 | `check-all -Only` 相关闸门 |
| V6 | Given 生成的部署样例，Then 必填项缺失时 compose 仍带变量名失败（`:?` 未被误删） | `docker compose config` 缺变量的反例：屏蔽开发机环境与已有 `.env`，其他必填变量用确定性测试值补齐，每次只移除一个被测变量，断言错误信息里的变量名 |
| V7 | Given 需要自定义路由 TTL 的部署，When 在 Compose override 里给 backend 注入原生键 `Leistd__MultiTenancy__Routing__CacheLifetime`，Then 非默认值生效 | 在同一部署入口下验证一次 |
| V8 | Given 部署配置任务，Then 从 `template/docs/README.md` 的按任务读取表能找到新子节，读取成本闸门通过且不提高上限 | `measure-template-read-cost.py --check` |

### 已确认的决策

用户 2026-10-24 确认以下事项：
- 执行审视结论 §5 的 1–4 项；
- 沿用此前的工作方式：方案交 Codex 审查直到明确通过，再由 Codex 实施并验证，Claude 审查代码，然后交付合并。
- 业务侧 2026-10-24 转达同一裁决：规则"作为新一节"写进 `settings.md`；完成后由 Claude 把提交、PR、CI 和业务三仓下次同步要做的事，追加到审视结论末尾的"实施结果"一节。crm Worker 基线由业务侧处理，不在本规格范围。

### 影响说明

- **T10、T13 移除了 `TENANT_ROUTE_CACHE_TTL` 这个部署变量入口。** 已经设置了该变量（如 `00:05:00`）的部署，同步模板后会静默回到 10 分钟。需要自定义时，改为在 Compose override 里直接给 backend 注入 `Leistd__MultiTenancy__Routing__CacheLifetime`；只写在 `.env` 里、没有 Compose 引用的话，不会进入容器。不提供兼容变量。

### 方案

- **实施顺序**：在一个分支里依次完成 T1–T13 的删除与注释迁移、`settings.md` 规则、Serilog 守卫测试和 csproj 注释。
- **JSON 条件块**：appsettings 的 `//#if` 块之间的逗号按删除后的结构重排；`Leistd` 节内删到只剩条件节时，要保证每种条件组合下的 JSON 都合法。
- **注释迁移**：各 Options 的 XML 注释吸收被删配置旁的说明，例如证书要求、访问令牌寿命的约束、外部登录凭据的注入方式。
- **索引同步**：在 `template/docs/README.md` 部署任务的按需栏引导到新子节。读取成本余量只有 1 字符，同步精简索引里的冗余文字守住上限，不扩大普通任务的必读集合，不提高上限。
- **版本与发布**：只改模板不会触发框架 beta，但本 PR 同时改了 `framework/` 下的 csproj 注释。发布分类（`scripts/plan-quality-checks.py` 的 `needs_release`）把所有非测试、非 Markdown 的 `framework/` 路径视为发布输入，Codex 已用分类逻辑确认为 `True`。因此合入 develop 并通过放行后会发一个框架 beta；模板正式包仍随 main 同号发布。保留同一 PR，不改分类器，不拆 PR。

## 4. 结果

### 实际交付

PR #167（分支 `zengqinglei/config-defaults-cleanup`，基于 develop `095037dd`）。

- **模板配置**：删除 T1–T13，保留列的项全部保留。
  - `appsettings.json`：条件逗号按删除后的结构重排；`Leistd` 节在没有任何子节的场景整体裁掉。
  - `.env.example`：两个外部登录开关改为注释示例。
- **注释迁移**：说明迁到 `OAuthServerOptions`、`ExternalAuthOptions` 的 XML 注释。`SettingConstant`、`SettingDefinitionProvider` 的注释改为"默认值取绑定后的 Options"。其余项已有 Options 或组件 XML 覆盖，复用不重复。
- **规范**：`template/docs/standards/settings.md` 新增 §1 子节"部署配置只写差异"；`template/docs/README.md` 部署任务行链接到新子节，并压缩冗余措辞守住读取成本上限。
- **测试**：`SettingsContractTests.Template_log_level_follows_the_setting_and_clearing_restores_the_baseline`。
- **框架**：`Leistd.MultiTenancy.Core.csproj` 注释改为新节名。

### 与计划的偏差

- 阶段 1 审查（Codex）四条意见全部采纳，规格据此修订：
  - 空值说法改为按类型与消费路径判断；
  - V1 限定为不使用被移除的变量，并补充 TTL 入口的影响说明与 V7；
  - 补充索引同步与 V8；
  - 发布预期定为会发框架 beta。
- 空值探针两边结果不一致。Codex 的反射 Binder 探针：bool、TimeSpan 绑定失败，int? 得到 null。Claude 的文件式程序探针（Binder 10.0.0 与 10.0.13）：都回落到属性初值。原因未查清，规范只写两边一致的结论"会遮蔽下层值"。
- 代码审查（Claude）一处：测试 `finally` 里的断言可能盖住原始失败，已改为只清理。
- PR 首轮 CI 失败：仓库门禁 `scripts/test-template-generation.py` 断言生成的 appsettings 带 `Leistd:MultiTenancy:Routing`，删除后报 `KeyError: 'Leistd'`，四片有效形态生成全红。这是改配置键时漏搜的仓库内消费方。修正为按仍保留的 `Leistd:ServiceClients:Identity` 判定"租户路由客户端配置只在多租户资源服务形态出现"，本地四片（320 个形态与对照片）及 `--self-test` 通过。

### 验收记录（证据在 `.tmp/config-defaults/`，未提交）

| 项 | 结果 |
| --- | --- |
| V1 | 9 条配置链、125 个观测值删除前后一致；旧 TTL 变量反例确认入口已移除 |
| V2 | 选择器给出 7 个场景，`-GenerateOnly` 通过；56 份 appsettings 严格解析通过；14 份 Compose 解析通过。另由 Claude 穷举 appsettings 的 9 个条件符号、512 种组合，都是合法 JSON，删一个逗号的变异有 464 种组合报错 |
| V3 | 新测试通过；删除基础档 `Serilog:MinimumLevel:Default` 后构建成功、断言失败（Debug / Information）；还原后通过 |
| V4 | 22 项设置相关集成测试通过（真实 PostgreSQL）；14 个设置默认值经接口读回，前后一致 |
| V5、V8 | 相关闸门通过；读取成本五类读数不变，上限未改 |
| V6 | 55 次缺变量反例，每次都带变量名失败 |
| V7 | 原生键覆盖绑定为 00:05:00，未覆盖时为 00:10:00 |

### 遗留项

- 合入 develop 后，csproj 注释会让发布分类判为框架发布输入，发布 `1.2.0-beta.<N>`；模板正式包随下一次 develop→main 同号发布。
- 业务三仓下次同步模板时，按"影响说明"处理 TTL 变量入口的移除。

### 已上收的常驻文档

`template/docs/standards/settings.md`、`template/docs/README.md`。
