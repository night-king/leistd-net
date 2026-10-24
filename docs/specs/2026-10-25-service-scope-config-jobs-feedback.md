# 机器 scope 接收侧入口、部署配置分层与周期任务观察者契约

状态：已完成（本地提交，未推送） · 风险档：中（模板授权入口公开化、规范与随包文档澄清，运行时行为不变） · 关联：待开 PR

## 1. 需求

### 问题与背景

CRM V2 三仓在适配模板 develop `0a3e7399` 时提交了一份反馈（机器 scope、部署配置、周期任务观察者、项目 Skill 路由），协调目录 `/Users/quincy/orca/workspaces/.crm-v2-adaptation/backlog-20261019/` 下：

- 回执 `framework-receipt-scope-config-jobs.md`：核实结论（含文件:行证据）、最终策略与实施策略，是本次实施的权威来源；
- 用户裁决 `framework-feedback-decisions-2fa-scope.md` 第二节（2026-10-25）：内部服务通讯不把权限控制得太细，机器 scope 不走配置、默认不细分。

核实基线为 develop `8fe7736f`，`0a3e7399` 之后涉及的文件没有改动。

### 条目与决定

| 编号 | 问题 | 决定 | 落位 |
| --- | --- | --- | --- |
| 1、E | 机器 scope：签发侧一个下游只有一条服务级 scope；接收侧的机器 scope 策略是私有方法，只在 Identity 多租户下存在，Resource 没有可复用入口，容易写成只判“是不是机器” | 签发侧**不加配置**，原回执提出的 `OAuth:ApiResources[].MachineScopes` 配置项**已撤回**（用户裁决）。原则改为内部机器调用默认服务级 scope，确有需要才按能力细分并以 MachineOnly 项登记在 `OAuthScopes.cs`。接收侧公开 `AddMachineScopePolicy(policyName, scope)`，所有 Bearer 形态可用 | 模板后端、`service-invocation.md` |
| 2 | 共用配置节的说明没有被拆分段落引用 | 基本不成立；补一句交叉引用 | `service-invocation.md` |
| 3、B、C | “默认同名”有歧义；Token Exchange 只允许一个 scope 没写；`appsettings.json` 与 compose 注释写“每项登记为同名 scope” | “默认同名”限定为签发方登记；调用方 `TokenExchange:Scope` 必须显式填写、只能一个；注释改为 Scope、OwnerClientId 可配置 | `service-invocation.md`、`appsettings.json`、`docker-compose.yml` |
| 4、D | compose 的 `${X:-}` 会遮蔽 Production 档；“占位”与“不置空”两条规则有张力；`:-false` 与代码默认值重复 | 规范写明分层：compose 最高层，它列出的键只由 `.env` 提供；“不置空”限定为覆盖层，基础档 `""` 占位是例外。`SMTP_ENABLE_SSL:-true` 保留并注释为有意的生产默认。**`:-false` 不删**，见下方偏差 | `settings.md`、`docker-compose.yml` |
| 5 | Started 也在持锁期间执行，随包文档只要求 Completed 自设超时，示例 Started 没有超时；模板规范没有指向随包文档的登记约束 | 随包文档与 XML 注释写明两个回调都在持锁期间、都要自设有限超时；框架仍不加超时选项（2026-10-23 规格的决定）。模板规范补放置命名与契约指引 | `framework/docs/components/background-jobs.md`、`IRecurringJobRunObserver`、模板 `background-jobs.md` |
| A | 随包示例 Started 插入后保存返回 id，Started 被取消或结果未知时 Completed 会补建一行重复记录 | 示例改为预先生成 Id，Started 插入、Completed 按 Id upsert | 随包文档 |
| 6 | 项目 Skill 描述含“框架用法咨询”，路由表和 `docs/README.md` 没有对应行 | 两处各加一行，指向 `backend/README.md` 的“Leistd 框架 API”一节 | 项目 Skill、`template/docs/README.md` |

### 验收标准

- Given 用 `AddMachineScopePolicy("p", "orders-api")` 登记的策略，When 经认证中间件与策略评估器判定真实验签的 Bearer，Then 只有 `sub` 为 `client:*` 且 `scope` 含 `orders-api`（含多值）的令牌通过；不带该 scope、带其他或同前缀 scope 的机器令牌、用户令牌都被拒（`MachineScopePolicyTests.Verified_bearer_tokens_are_admitted_only_for_machines_with_the_required_scope`）。
- Given 会话方案恢复出 claim 满足断言的主体、请求没有 Authorization 头，When 按该策略判定，Then 被拒，且对照证明拒绝来自认证方案收窄（`A_session_principal_without_a_bearer_token_is_rejected`）；超管 claim 不能旁路（`Super_admin_claim_does_not_admit_the_machine_policy`）。
- 上述用例在 Identity 与 Resource 两种形态都生成并通过；Standalone 不生成该入口与测试。
- Given Identity 多租户，When 租户控制面两条策略改为调用新入口，Then 既有判定矩阵与完整 HTTP 用例（`AuthenticationModeTests`、`ComponentEndpointAuthenticationTests.Machine_endpoints_admit_only_a_bearer_with_the_matching_scope`）不变（CI 验证，见 §4）。
- 文档改动通过源码与生成模式的引用、锚点检查；compose 键与 `.env.example`、Options 目录的一致性由生成闸门四片验证。

### 方案

接收侧入口是 `Api/DependencyInjection.cs` 上的 `IServiceCollection` 扩展：经 `AddOptions<AuthorizationOptions>().Configure<IOptions<ClaimTypeOptions>>` 登记策略，方案固定为 `OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme`（Identity 的本地校验与 Resource 的远端校验都用它，带浏览器会话的 Resource 也是 Smart 方案转发到它）。判定沿用原私有方法：`ClientSubject.IsMachine` 加按空格拆分的 `scope` claim 比对。守卫为 `OpenIddictServer || RemoteTokenAuth`；Standalone 没有 Bearer，不生成。命名沿用原私有方法名与回执约定的签名。

## 3. 计划

| 任务 | 产物 | 验证 |
| --- | --- | --- |
| 公开机器 scope 策略入口，租户路由两条改为调用 | `template/backend/src/CompanyName.ProjectName.Api/DependencyInjection.cs` | 五个场景构建；单测 |
| 机器策略测试（两种形态） | `template/backend/tests/CompanyName.ProjectName.UnitTests/Api/MachineScopePolicyTests.cs`，`template.json` 在非 Bearer 形态排除 | 生成场景单测；变异检查判据可证伪 |
| 服务间调用规范 | `template/docs/standards/service-invocation.md` | 源码与生成模式引用、锚点检查 |
| 配置注释、compose 与设置规范 | `appsettings.json`、`deploy/docker-compose.yml`、`template/docs/standards/settings.md` | 生成闸门四片 |
| 观察者契约 | 随包 `background-jobs.md`、`IRecurringJobRunObserver.cs`、模板 `background-jobs.md` | 构建 Core；引用检查 |
| Skill 与索引路由 | 项目 Skill 路由表、`template/docs/README.md` | Skill 校验、锚点检查 |

不做：签发侧配置项（已撤回）；框架超时选项；compose 闸门（一行规则能说清）。

## 4. 结果

- 交付：本地提交于分支 `zengqinglei/scope-config-jobs`（基于 develop `8fe7736f`），未推送、未开 PR。
- 新增公共方法：`DependencyInjection.AddMachineScopePolicy(this IServiceCollection, string policyName, string requiredScope)`（模板 Api 层，非框架包）。
- 与计划的偏差：
  - **第 4 条没有删除 `:-false`**。实测 `Microsoft.Extensions.Configuration.Binder` 10.0 把空串绑定到 `bool` 抛 `InvalidOperationException`（Failed to convert configuration value '' ... to type 'System.Boolean'）。`ExternalAuthOptions.Enabled`、`UserRegistrationPolicy.EnableEmailVerification` 都是非空 `bool`，去掉缺省后 `.env` 未填时 compose 注入空串，宿主启动失败，属于“默认就会出错”。改为在 `settings.md` 写明：compose 的布尔与数值键要么写非空缺省、要么用 `:?` 必填，与代码默认相同的缺省也保留。
  - 验证没有走 `template-dev-workspace.ps1` 与 `validate-skills.ps1`：本会话的沙箱拒绝执行 pwsh。改为在 `.tmp/scope-dev` 下用与脚本相同的 `dotnet new --debug:custom-hive` 生成场景，消费已发布的 `Leistd.*` 1.2.2（框架 csproj 未改，模板引用的就是该版本）；Skill 校验按脚本规则人工核对（frontmatter、行数 59 ≤ 120、标记与引用路径未变）。
  - 测试放在 UnitTests 而不是集成测试：不需要数据库，经认证中间件与 `IPolicyEvaluator` 走与授权中间件相同的路径，令牌沿用 `ServiceInvocationTests` 的造法（`JsonWebTokenHandler` + 测试签名密钥）。
- 验收记录：
  - 构建：identity-all-features、resource、resource-capabilities-03、identity-capabilities-01、standalone 全部成功；IDE0001/IDE0002 格式检查（identity-all-features、resource）通过。
  - 单测：identity-all-features 239/239、resource 98/98、resource-capabilities-03 98/98、identity-capabilities-01 184/184、standalone 190/190；`MachineScopePolicyTests` 在四个 Bearer 场景各 8 条通过，standalone 不生成。变异：只去掉 scope 判定时 3 条机器反例失败；再去掉方案收窄时会话用例与两条正例失败（Bearer 不再被认证）。
  - Resource 的 `AuthenticationModeTests.Resource_default_and_current_user_policies_*` 5 条通过（不需要数据库）。
  - 文档：源码模式引用检查 115 份通过；生成模式引用与锚点检查（identity-all-features、resource、standalone、resource-capabilities-03）通过。
  - 生成闸门四片（`scripts/test-template-generation.py`）：shard 0 107 形态、shard 1 107 形态、shard 2 106 形态（共 320）、control（768 组输入映射、10 组等价比较、4 个 Ci 变体、10 个 verify.ps1 变体）全部 PASS，含 compose 键与 `.env.example`、Options 目录的一致性。
- 遗留：
  - 需要 PostgreSQL 容器的集成测试（Identity 多租户的租户控制面判定矩阵与完整 HTTP 用例）本地未运行，按约束不起容器，由 PR CI 验证。
  - 框架随包文档改动按版本规范会触发框架 beta；未打包核对包内文档。
- 常驻文档：`template/docs/standards/service-invocation.md`、`settings.md`、`background-jobs.md`、`template/docs/README.md`、项目 Skill 路由表、`framework/docs/components/background-jobs.md` 已同步。
