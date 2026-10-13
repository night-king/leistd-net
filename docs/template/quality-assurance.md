# 模板质量验证

本文只记录模板维护相对[质量检查与验证分工](../framework/quality-assurance.md)的差异，不进入生成项目。闸门、检查接替与变异验收、CI 计划、回执、源码预检与 docs-only 规则以该文为准；生成项目自身的测试规则见模板 [`testing.md`](../../template/docs/standards/testing.md)；生成与条件裁剪入口见[模板开发规范](./development-guide.md)。

## 本地与 CI 的执行位置

| 阶段 | 验证责任 |
| --- | --- |
| 编辑中 | 已生成项目的目标测试；真实依赖变化做目标集成 |
| 完整改动形成后 | 局部源码按[本地集合](../framework/quality-assurance.md#模板本地场景集合)计算产品并运行完整适用阶段；全局生成/共享/未知输入保留完整 PR 档 |
| PR CI | 按实际候选计划完成必过责任；审查可并行，合并前同时通过 |
| 合入、夜间与发布 | 代码/共享输入和发布保持完整档；已验证基线的非发布文档推送按根质量规范选择责任 |

源码中的修复最终必须写回模板并重新生成验收。静态条件覆盖证明不了产物能编译、lint 或运行；不能只挑一个默认形态，也不能将本地场景集合当作 CI 阶段裁剪计划。审查与失败重跑规则引用根质量规范，不重复维护场景清单。

## 测试层级与接替边界

UnitTests 验证隔离规则和边界、注册生命周期/幂等；IntegrationTests 用 `WebApplicationFactory` 组合真实 HTTP 管道。后者是进程内宿主，替换外部依赖后不能认证真实网络、OIDC 跨服务链路或 PostgreSQL 语义。浏览器模式组件测试验证类、注入和 DOM，HTTP mocks 与 TestBed 仍是测试替身，不等于完整应用 E2E。纯函数不启动 TestBed；需要 DOM/注入上下文时才创建 fixture，不以固定数量比例削减测试。

测试宿主的口令工作因子与宿主复用口径见生成项目 `testing.md` §2.1；PostgreSQL 与 OIDC 端到端使用生产默认工作因子。

集成测试与矩阵运行时冒烟都跑在真实 PostgreSQL 上（选型依据见[模板开发规范 §9](./development-guide.md#9-测试与开发只用-postgresql为什么不用-inmemory-或-sqlite)，集成测试的建库方式见 `testing.md` §2.1）；冒烟在本次矩阵运行共用的容器里为每个场景建库，先用该场景构建出的 `DbMigrator --apply` 迁移（Resource 用 `MigrationTarget` 单目标，不回源 Identity），再启动 API。矩阵与集成测试因此都需要本机 Docker 引擎（数据库端口绑定在 127.0.0.1，远端上下文不适用），CI 的 ubuntu runner 自带。设计时快照比对仍保留在单元测试里，它不需要 Docker、几毫秒给出结论。PostgreSQL 端到端承担跨进程与多库拆分；真实库集成测试覆盖了某个路由断言，不代表端到端里的对应断言都能删除，逐项接替仍需变异证据。不能为了测试新建只有测试调用的生产 Provider 选择抽象。

条件 using 守卫覆盖 768 个原始符号赋值，生成矩阵覆盖登记场景，范围不同。默认前端保留真实 Chromium、隔离与完整 spec 发现；未经等价反例验证，不关闭隔离、改 jsdom 或删除发现/翻译/形态断言来提速。新增场景要验证特性组合，不能只测 Identity/Resource 正常路径。

场景定义、`pr`/`full` 档位及具名分片在 `scripts/template-matrix-scenarios.ps1`，定义、全集与分片必须一致；每片至少一个场景。人工验证用 `scripts/test-template-matrix.ps1` 的 `-Scenarios`、`-Tier pr`（可加 `-Slice <片名>`）或不带参数的全集；CI 每片一个作业，使用 `-Tier <档位> -Slice <片名>`，不能与 `-Scenarios` 混用。`pr` 档只收覆盖必需的场景：`check-template-scenario-coverage.py` 对全部模板文件求值文件级 modifiers、嵌套条件与 computed 符号，任何条件行只由 `full` 档独有场景生成即失败。它同时检查行覆盖和有效能力两两组合：同一文件里几处分支只在某个 `full` 档场景里同时出现时（例如外部登录与非本地化的两段登录页代码），二者的组合交互要到合入后才验证。新增条件分支让闸门变红时，把对应场景加入 `pr` 档或改写条件，不删除闸门；加入后按实测耗时放入合适的分片，必要时新增具名分片并写明它验证什么。默认形态 `identity` 不是覆盖必需，但它是 `dotnet new` 的默认产物，固定留在 `pr` 档。独立的全部有效形态生成作业必须在同候选汇总中成功；内部文档变更时按既有范围规则记为不适用。Resource 纯 API 的 Lint/Frontend/Test 阶段明确记为 not-applicable，汇总按场景元数据核对，拒绝伪造 pass。默认人工及 full 档每个入选场景保留生成形态、还原/构建、运行时冒烟、后端单测/集成测试、lint、前端构建、全部 spec 发现与浏览器测试。直接 PR 的局部计划按[同候选输入计划](../framework/quality-assurance.md#同候选输入计划)选择必要阶段，不能用手动跳过代替计划。容器检查由 `-ContainerSmoke` 挂到两档共有的含前端 Standalone 与纯 API Resource 场景，清单使用 `$ContainerScenarios`；PR 按完整 base→head 差异判定，范围不明时执行容器验证。

矩阵验收读取同一次 run 各分片的 artifact，使用 `scripts/check-template-matrix-results.ps1 -Tier <档位>` 核对候选 SHA、收据档位、独立预期计划的准确场景归属与 Backend/Runtime/Lint/Frontend/Test 状态，以及登记 `Verify` 的场景在完整模式下 `Verify=pass`（缺失或其他值即失败；其余为 `not-run`）（CI 传 -ValidationPlanPath；默认要求完整档）；要求容器验证时传入 `-ContainerSmoke`。还须核对日志中后端和真实浏览器测试确有用例执行，不能仅凭作业名称或退出码推定完整覆盖。Standalone 容器检查构建 API 与 Migrator 镜像，并在两种镜像内执行 `dotnet --info`；它认证镜像构建与 .NET 运行时可用，不代表 API 已按部署配置启动或已连接数据库。纯 API Resource 另外在镜像内实际迁移并启动 API，验证 liveness、无 SPA 与无 Node；跨服务认证及完整 PostgreSQL 隔离仍由各自独立作业承担，未执行的可选场景另行注明。

分片均衡同时计入实际承担的容器阶段、生成准备和浏览器安装，不只按场景数量划分。关键片可能随实测改变，调整前先核对各场景命令区间和准备阶段；准备阶段变快、未产生 lint 热缓存或另一片提前完成，都不能当作删减测试的依据。

OIDC 浏览器闭环复用 `scripts/test-template-oidc-e2e.ps1`：`-IncludeBrowserScenarios` 在 HTTP 场景后追加有头浏览器验证，`-BrowserOnly` 只执行浏览器闭环。另需 Node.js、npm 与已安装 Chromium 的 agent-browser；可用官方 `AGENT_BROWSER_EXECUTABLE_PATH` 选择浏览器程序。脚本按共享矩阵定义生成 Identity 外部登录与 Resource，生产构建前端由所属 API 同源托管；覆盖登录、服务端票据真实到期后的续期/拒绝（浏览器阶段把 Identity 切到快速档 `OAuth__AccessTokenLifetime=00:01:30`，签发寿命按档位断言，不等默认的 10 分钟；分层原则见[质量检查与验证分工](../framework/quality-assurance.md#分层执行与时间预算)）、官方退出、Google/GitHub 官方处理器及接续原授权请求。第三方通信使用本地 PKCE 协议夹具，不替换生产认证处理器。一次性浏览器会话关闭自动保存，避免额外访问已见过的源。证据保存在隔离 `.tmp/oidc-e2e/<run>/`，包含断言、脱敏 HAR、截图和 SQL 投影；浏览器会话与容器仅清理本轮创建的资源。浏览器阶段的 Resource 同时启用通知与业务实时，真实创建角色并验证已打开列表自动刷新、共享一条 Hub 连接；多租户阶段通过正式管理员 CLI 引导授权。CI 默认仍执行 HTTP 入口，浏览器开关按需显式启用。

默认 HTTP 端到端还生成启用业务实时的纯 API Resource，以正式管理员命令授予权限，再由独立 .NET SignalR 客户端通过真实 Bearer/WebSocket 订阅宿主角色列表。实际创建角色后验证提交后的事件，并拒绝跨作用域、未知资源及无作用域资源键的订阅；同时验证没有前端目录且根路径不提供 SPA。该用例认证外部客户端消费能力，浏览器列表自动刷新由上述浏览器场景另行验证。

## 业务维护 Job

框架的调度循环测试见[覆盖分工](../framework/quality-assurance.md#定时任务的覆盖分工)。模板只验证业务职责：清理汇总的失败/未解析数据库传播放无宿主单测；SQL 翻译、全局过滤器、共享租户与真实 DI 接线放现有 PostgreSQL 集成测试类。

测试写法见生成项目 `testing.md` §2.3；默认测试工厂仍启动调度器。模板当前的截止契约：通知读/未读分别按保留期清理，`CreationTime < cutoff`（等于保留），且覆盖宿主和共享租户；会话是 `LastSeenTime <= cutoff`（等于删除），同时核对领域 `IsExpired`。断言按本用例标识筛选。

不为每个 Job 新建容器、独立场景或协议 E2E，不以有 HTTP 集成测试为由删除上述边界。代表性生成覆盖 LocalIdentity 与 RemoteTokenAuth 通知、LocalIdentity 无通知侧；全集 CI 继续覆盖所有适用形态。新用例首先解决覆盖缺口，耗时同时披露入口与方法体，不把增加测试宣称为提速。

## 源码预检、CI 计划与局部 PR

源码预检、CI 接替、docs-only 例外、同候选输入计划与回执的唯一规则见[质量检查与验证分工](../framework/quality-assurance.md#同候选输入计划)。`template/` 下的文档和 Skill 属于生成载荷，须验证全部有效形态的实际文档契约；Spartan 登记表额外触发前端依赖闸门。文档与局部源码同改叠加文档责任，保留已证明的场景与阶段选择。

## 前端依赖维护

默认与本地化载荷的 Angular 运行时、CDK、编译器和构建工具须按官方 peer 范围配套更新，并同时更新两套锁文件。先在隔离副本验证安装、原审计阈值、lint、构建与真实浏览器测试，再同步依赖文件；完整条件矩阵仍须执行。不得用 `--force`、`--legacy-peer-deps` 或降低审计阈值绕过依赖冲突与安全门禁。

审计口径：门槛是生产依赖 high/critical 为 0（`npm audit --omit=dev --audit-level=high`），矩阵与生成项目 `verify` 的 `frontend-audit` 步骤同口径，不是全部 severity 总数为 0。生产审计通过不代表工具链或部署产物没有漏洞。

开发工具链的公告不阻断门槛，但须逐条记下：哪个工具引入、用途、相关输入能否到达、为何暂时接受、何时复查。"没有可达路径"与"没有兼容修复"是两种理由，分开写；没有修复不能单独证明不可达，dev 标记也不等于安全。2026-10 的现状：

| 公告 | 引入路径 | 情况 | 复查时机 |
| --- | --- | --- | --- |
| `braces` 3.0.3（[GHSA-vfj7-8cjw-p6xm](https://github.com/advisories/GHSA-vfj7-8cjw-p6xm)） | Stylelint 与 Spartan CLI→`ts-morph` 经 `micromatch` 引入，只在本地 lint 与组件生成时匹配仓库内的 glob | 上游无补丁版本 | 上游发布修复或 Stylelint/Spartan CLI 换掉该依赖时 |
| `undici` 7.29.0（如 [GHSA-rfgv-xxqx-mfg5](https://github.com/advisories/GHSA-rfgv-xxqx-mfg5)） | Nx 23.3.0 精确依赖，经 Spartan CLI 引入，属于本地开发与 CI 都会执行的开发工具链（Spartan CLI 的 info/healthcheck 与组件生成），不进入生产前端运行载荷。Nx 只在其 http-client 发起请求（Nx Cloud、`nx release` 的 GitHub 客户端等）且设置了 `HTTP(S)_PROXY`/`ALL_PROXY` 时才加载 undici，输入是远端服务的响应；本仓不配置 Nx Cloud 或 `nx release` | 7.29.1 已有修复，但被 Nx 精确钉住。没有证明所有 CLI 路径都不可达；考虑到维护成本暂时接受，不强行 override 父工具链钉住的版本 | Nx 放宽或升级该依赖时，与 Spartan CLI 配套升级 |

清零方式按本项目的维护取舍：不用 `npm audit fix --force` 降级 Angular/Spartan/Nx，不加未经验证的 `overrides` 强行替换父工具链钉住的版本，优先等上游后配套升级；确需 override 时按上一段的隔离验证执行并写明退出条件。这是控制维护成本的决定，不是 npm 的规定。

## lint 缓存与失效

默认与本地化载荷的 `npm run lint` 都对原完整范围执行 ESLint、Stylelint 和 Prettier，使用各工具官方的 content cache，保留失败退出码与现有规则。缓存只复用未变文件的有效结果，不改为 Git diff 清单；新增文件仍会发现，配置变化按工具的实际配置/选项重新检查。

缓存位于生成前端的 `.cache/lint/`。每次依赖安装通过 `postinstall` 清掉该目录，覆盖插件更新不一定进入工具缓存键的问题；这是唯一的失效入口，不增加自写缓存键计算器。该路径只存工具结果，不放源码；Git、Prettier 以及模板载荷排除它。显式忽略生命周期脚本安装依赖时，使用者须先删除该目录，不能复用上一套依赖的 lint 结果。

依赖、配置或检查入口变化后，在已建缓存上注入 TS/HTML/CSS/格式违规和新增文件；配置收紧、插件更新后未改源文件的违规也须变红。测试真实 npm 安装生命周期会清缓存，不仅调用一个模拟清理函数。当前 ESLint 未启用跨文件类型检查；将来引入 type-aware 或跨文件规则时，先证明缓存依赖范围完整，否则对该检查重新采用全量执行。

本地同目录反复校验有热缓存收益；矩阵每次生成到新 run 目录，首次执行仍检查全量。不共享缓存目录或假定每片都热命中，不把本地收益乘场景数当作 CI 收益。Playwright 浏览器安装/缓存策略须独立测量，不把下载时间全部当成可省值。

官方依据：[ESLint 缓存](https://eslint.org/docs/latest/use/command-line-interface#--cache)、[Stylelint 缓存](https://stylelint.io/user-guide/cli/#--cache)、[Prettier 缓存与插件限制](https://prettier.io/docs/cli#--cache)、[Angular 组件测试](https://angular.dev/guide/testing/components-basics)、[Angular test 参数](https://angular.dev/cli/test)、[Playwright CI](https://playwright.dev/docs/ci)。

浏览器认证变更在 L1 跑 `-Tier pr`，全部登记场景由合入后的 `full` 档承担。修改令牌寿命、刷新或到期判据时，提交前还须运行 `pwsh scripts/test-template-oidc-e2e.ps1 -IncludeExpiryWait`，验证真实到期边界；PR CI 默认省略真实到期等待，full 显式补跑，不能用 PR 成功代替这项变更责任。真实官方处理器、Cookie 解保护、服务端票据删除、并发刷新、资源归属与可编译变异的边界见[浏览器认证维护规则](browser-authentication.md)。
