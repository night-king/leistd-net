# 模板质量验证

本文只记录模板维护相对[质量检查与验证分工](../framework/quality-assurance.md)的差异，不进入生成项目。闸门、删除与替换的验收、CI 计划、回执、源码预检与 docs-only 规则以该文为准；生成项目自身的测试规则见模板 [`testing.md`](../../template/docs/standards/testing.md)；生成与条件裁剪入口见[模板开发规范](./development-guide.md)。

## 本地与 CI 的执行位置

本地只产出定向证据，配方以[本地验证配方](../framework/quality-assurance.md#本地验证配方)为准；本文不另列一份。

| 阶段 | 验证责任 |
| --- | --- |
| 编辑中 | [模板开发工作区](../framework/quality-assurance.md#模板开发工作区)里的目标测试类或 spec；真实依赖变化做目标集成 |
| 完整改动形成后 | 工作区 `-Sync` 后按配方执行目标测试、lint 与必要的浏览器验证；多场景完整阶段、全量 spec 与端到端交给 CI |
| PR CI | 按实际候选计划完成必过责任；用户要求审查时可并行 |
| 合入、夜间与发布 | 代码/共享输入和发布保持完整档；已验证基线的非发布文档推送按根质量规范选择责任 |

修复写在模板源里，再 `-Sync` 进工作区验证；工作区的修改不复制回模板。静态条件覆盖证明不了产物能编译、lint 或运行，条件分支与裁剪由 CI 在登记场景上实际生成验证。本地全量矩阵只用于复现 CI 失败（`-Scenarios <失败场景>`）。审查与失败重跑规则引用根质量规范，不重复维护场景清单。

## 测试层级与接替边界

UnitTests 验证隔离规则和边界、注册生命周期/幂等；IntegrationTests 用 `WebApplicationFactory` 组合真实 HTTP 管道。后者是进程内宿主，替换外部依赖后不能认证真实网络、OIDC 跨服务链路或 PostgreSQL 语义。浏览器模式组件测试验证类、注入和 DOM，HTTP mocks 与 TestBed 仍是测试替身，不等于完整应用 E2E。纯函数不启动 TestBed；需要 DOM/注入上下文时才创建 fixture，不以固定数量比例削减测试。

测试宿主的口令工作因子与宿主复用口径见生成项目 `testing.md` §2.1；PostgreSQL 与 OIDC 端到端使用生产默认工作因子。

集成测试与矩阵运行时冒烟都跑在真实 PostgreSQL 上（选型依据见[模板开发规范 §9](./development-guide.md#9-测试与开发只用-postgresql为什么不用-inmemory-或-sqlite)，集成测试的建库方式见 `testing.md` §2.1）；冒烟在本次矩阵运行共用的容器里为每个场景建库，先用该场景构建出的 `DbMigrator --apply` 迁移（Resource 用 `MigrationTarget` 单目标，不回源 Identity），再启动 API。矩阵与集成测试因此都需要本机 Docker 引擎（数据库端口绑定在 127.0.0.1，远端上下文不适用），CI 的 ubuntu runner 自带。设计时快照比对仍保留在单元测试里，它不需要 Docker、几毫秒给出结论。PostgreSQL 端到端承担跨进程与多库拆分；真实库集成测试覆盖了某个路由断言，不代表端到端里的对应断言都能删除：删除前逐条指出覆盖同一行为的测试，属于安全判据的另做变异验证。不能为了测试新建只有测试调用的生产 Provider 选择抽象。

条件 using 守卫覆盖 768 个原始符号赋值，生成矩阵覆盖登记场景，范围不同。默认前端保留真实 Chromium、隔离与完整 spec 发现；未经等价反例验证，不关闭隔离、改 jsdom 或删除发现/翻译/形态断言来提速。新增场景要验证特性组合，不能只测 Identity/Resource 正常路径。

场景定义、`core`/`full` 档位及具名分片在 `scripts/template-matrix-scenarios.ps1`，定义、全集与分片必须一致；每片至少一个场景，`core` 每片恰好一个场景。人工复现 CI 失败只用 `scripts/test-template-matrix.ps1 -Scenarios <失败场景>`；CI 每片一个作业，使用计划的 `-Tier <档位> -Slice <片名>`，不能与 `-Scenarios` 混用。`core` 是 PR 的代表场景（默认产物 `identity`、Verify 场景 `identity-all-features`、两个容器场景），PR 在其上只承担[增量保证](../framework/quality-assurance.md#pr-增量保证)：改动文件的 `core` 消费者，加上让被改条件块每一臂在开启侧、条件文件被裁掉的一侧各生成一次所需的最少场景；`template.json` 或共享输入变化时改跑代表场景冒烟（`core` 全部场景完整阶段），模板全集留给合入后的 `full`。全局覆盖只由 `full` 证明：`check-template-scenario-coverage.py` 对全部模板文件求值文件级 modifiers、嵌套条件与 computed 符号，要求 `full` 登记场景生成每个可达条件行，并覆盖有效能力的全部可达两两组合。同一文件里几处分支只在某个非 `core` 场景里同时出现时（例如外部登录与非本地化的两段登录页代码），二者的组合交互要到合入后的 `full` 才验证，这是有意的责任迁移。新增条件分支让闸门变红时，补登记场景或改写条件，不删除闸门；场景一个不删，`full` 的成员按实测耗时区间均衡分组（依据见[CI 划分与分片](../framework/quality-assurance.md#ci-划分与分片)）。全部有效形态生成作业拆成四片：三片按序号取模生成 320 个形态，对照片承担 768 组原始输入映射、角色等价、Ci 变体与全部 `verify.ps1` 变体的检查，并自行重新生成所需的代表形态；四片都必须在同候选汇总中成功，汇总核对四片同一候选与源码、形态无缺号无重复、对照片证明齐全。只改框架时不跑形态生成；内部文档变更时按既有范围规则记为不适用。Resource 纯 API 的 Lint/Frontend/Test 阶段明确记为 not-applicable，汇总按场景元数据核对，拒绝伪造 pass。默认人工及 full 档每个入选场景保留生成形态、还原/构建、运行时冒烟、后端单测/集成测试、lint、前端构建、全部 spec 发现与浏览器测试。直接 PR 的局部计划按[同候选输入计划](../framework/quality-assurance.md#同候选输入计划)选择必要阶段，不能用手动跳过代替计划。容器检查由 `-ContainerSmoke` 挂到两档共有的含前端 Standalone 与纯 API Resource 场景，清单使用 `$ContainerScenarios`；PR 按完整 base→head 差异判定，范围不明时执行容器验证。

矩阵验收读取同一次 run 各分片的 artifact，使用 `scripts/check-template-matrix-results.ps1 -Tier <档位> -CandidateVersion <候选版本>` 核对候选 SHA、候选包版本（CI 取 `framework-pack` 的 `candidate.json`）、收据档位、独立预期计划的准确场景归属与 Backend/Runtime/Lint/Frontend/Test 状态，以及登记 `Verify` 的场景在完整模式下 `Verify=pass`（缺失或其他值即失败；其余为 `not-run`）（CI 传 -ValidationPlanPath；默认要求完整档）；要求容器验证时传入 `-ContainerSmoke`。回执另记录真实 Chromium 执行的前端用例数 `BrowserTests`，前端测试为 pass 时须大于 0；以及 verify 内 Playwright 端到端最终通过的用例数 `E2ETests`（passed 加重试后通过的 flaky，每个用例计一次；命令非零退出仍失败），本地身份（非 Resource）场景 `Verify=pass` 时须大于 0，其余为 0（纯 API 与 Resource 前端没有浏览器端到端）；后端测试确有用例执行仍须核对日志，不能仅凭作业名称或退出码推定。放行候选须满足的全部责任以[放行责任表](../framework/quality-assurance.md#放行责任)为准。Standalone 容器检查构建 API 与 Migrator 镜像，并在两种镜像内执行 `dotnet --info`；它认证镜像构建与 .NET 运行时可用，不代表 API 已按部署配置启动或已连接数据库。纯 API Resource 另外在镜像内实际迁移并启动 API，验证 liveness、无 SPA 与无 Node；跨服务认证及完整 PostgreSQL 隔离仍由各自独立作业承担，未执行的可选场景另行注明。

分片均衡同时计入实际承担的容器阶段、生成准备和浏览器安装，不只按场景数量划分。关键片可能随实测改变，调整前先核对各场景命令区间和准备阶段；准备阶段变快、未产生 lint 热缓存或另一片提前完成，都不能当作删减测试的依据。

OIDC 端到端（`scripts/test-template-oidc-e2e.ps1`）只放跨进程才能证明的 HTTP 链路：多个 Resource 共用的框架实现只验 orders 一个服务，协议规则的变体（令牌交换的各条拒绝、Resource 验签的签发方/受众/有效期/未签名）在生成项目集成测试里。浏览器认证不另建仓库级浏览器闭环。生成项目的 Playwright E2E 承担两条主路径：组织要求两步验证时在页面上完成设置并登录，以及会话不够新时在对话框里确认身份后保存（`template/frontend/e2e/`，tag 为 `@auth`）。后者借模拟登录的会话达到"不够新"，不等真实时间，也不加只给测试用的服务端入口，因此只在能模拟登录的形态（多租户且开启操作记录）生成。它们计入回执的 `E2ETests`，见[放行责任](../framework/quality-assurance.md#放行责任)。CI 上失败用例最多重试两次，首次重试时抓 trace；重试后才通过的标为 flaky，不阻断，但矩阵日志和运行摘要会给出警告，报告与 trace 在本片的 `e2e-report-<片>` 产物里，成功时也保留。flaky 不说明失败来自环境，也可能是产品竞态，需要排查。本地不重试。其余浏览器语义按层下沉：Cookie、服务端票据、续期与拒绝、退出与确认页、外部登录协议由生成项目集成测试承担，界面交互由前端浏览器模式组件测试承担，逐项去向见 [浏览器认证维护规则](browser-authentication.md#测试分工)。Google、GitHub 真实登录改为手工验证。

默认 HTTP 端到端还生成启用业务实时的纯 API Resource，以正式管理员命令授予权限，再由独立 .NET SignalR 客户端通过真实 Bearer/WebSocket 订阅宿主角色列表。实际创建角色后验证提交后的事件，并拒绝跨作用域、未知资源及无作用域资源键的订阅；同时验证没有前端目录且根路径不提供 SPA。该用例认证外部客户端消费能力；浏览器里的列表刷新与断线后补查由前端组件测试承担（`roles.spec.ts`、`signalr-service.spec.ts`）。

## 业务维护 Job

框架的调度循环测试见[覆盖分工](../framework/quality-assurance.md#定时任务的覆盖分工)。模板只验证业务职责：清理汇总的失败/未解析数据库传播放无宿主单测；SQL 翻译、全局过滤器、共享租户与真实 DI 接线放现有 PostgreSQL 集成测试类。

测试写法见生成项目 `testing.md` §2.3；默认测试工厂仍启动调度器。模板当前的截止契约：通知读/未读分别按保留期清理，`CreationTime < cutoff`（等于保留），且覆盖宿主和共享租户；会话是 `LastSeenTime <= cutoff`（等于删除），同时核对领域 `IsExpired`。断言按本用例标识筛选。

不为每个 Job 新建容器、独立场景或协议 E2E，不以有 HTTP 集成测试为由删除上述边界。代表性生成覆盖 LocalIdentity 与 RemoteTokenAuth 通知、LocalIdentity 无通知侧；全集 CI 继续覆盖所有适用形态。新用例首先解决覆盖缺口，耗时同时披露入口与方法体，不把增加测试宣称为提速。

## 源码预检、CI 计划与局部 PR

源码预检、CI 接替、docs-only 例外、同候选输入计划与回执的唯一规则见[质量检查与验证分工](../framework/quality-assurance.md#同候选输入计划)。`template/` 下的文档和 Skill 属于生成载荷，须验证全部有效形态的实际文档契约；Spartan 登记表额外触发前端依赖闸门。文档与局部源码同改叠加文档责任，保留已证明的场景与阶段选择。

## 模板包与来源标记

模板以 `Leistd.Templates` 包分发，打包项目是 `template-package/Leistd.Templates.csproj`，发布规则见[模板包发布](../framework/versioning.md#模板包发布template-通道)。

- **来源标记** `template/.leistd/template.json` 是模板载荷里的普通文件，由 `dotnet new` 的符号替换写入：`package`（`id`、`version`、`sourceSha`）、`sdkVersion`、`name`（`-n`）、`parameters`（全部 `parameter` 符号的取值，布尔为 JSON 布尔）、`userSecretsIds`（各项目文件相对路径 → 实际生成的 GUID，与 csproj 引用同一个 `guids` 符号）。版本与源提交是 `template.json` 的两个 constant 符号（`TemplatePackageVersion`、`TemplateSourceSha`），源码里保留占位 `0.0.0-source` 与全零 SHA，打包时写入本次版本与源提交；SDK 由 bind 符号 `host:dotnet-cli-version` 取得。每个参数在标记里有对应的 `Marker<参数名>` 派生符号（布尔取小写）；新增参数时同时加派生符号与标记字段，生成作业逐形态核对标记的参数集合与取值，漏加即失败。
- **形态断言**：`test-template-generation.py` 在每个形态上解析标记并核对身份、名称、原始参数取值、UserSecretsId 与 SDK；比较产物字节（角色等价、Ci 变体、形态摘要）时把标记的 GUID 与 `parameters` 归一化，原始输入由逐项核对承担。矩阵的 `Assert-ScenarioShape` 把标记并入每个场景的 Present 与 RequiredTokens，`Assert-ScenarioMarker` 再逐项核对。
- **重建确定性**：同一模板版本、相同输入重建的产物，把 UserSecretsId 换回标记记录的值后与原产物零差异；由模板包验证的重建零差异测试承担，同步时不再重复检测。`template/scripts/template-marker.ps1`（随生成项目分发）提供读取标记、按标记重建（隔离模板目录）与目录比较。
- **模板包验证** `pwsh template-package/test-template-package.ps1`：打包（或检查给定 nupkg），核对包内文件集合等于 `git ls-files template`、包内 `template.json` 只改了两个身份符号；装进隔离模板目录后在仓库之外生成 `identity-all-features` 与 `resource-capabilities-03`，核对标记（含改动标记、缺少标记的反例），按标记重建并要求零差异（反例：不归一化时差异恰好是 UserSecretsId 所在文件，改动任一文件被检出）。CI 在模板生成对照片里运行它；发布时加 `-NuGetOrgRestore`。首个模板版本只验收这条本地 nupkg 链路，不提前公开发布。脚本开头先跑 nuget.org 还原重试分类器的夹具（NU1102/NU1103 指向默认版本与 NU1301 重试，NU1101、非默认版本、非 Leistd 包、混合与无错误码失败）。
- **三方同步** `template/scripts/sync-template.ps1`（随生成项目分发，用法由项目 Skill 的“同步模板”意图承载）：按标记在隔离模板目录分别生成旧版基线与新版目标，新版不接受标记里的参数或取值时指向该版本 Release 失败；基线与目标记成项目仓库里两个不挂分支的提交，由 `git cherry-pick --no-commit` 合并进工作区与暂存区，文本、二进制、删除/修改与改名（含只改大小写）都按 git 的合并规则处理，冲突即 git 的未合并路径。git 合并会覆盖被忽略的本地文件，所以目标新增的路径上已有被忽略的文件时在合并前失败。依赖清单合并后重新生成 npm 锁文件。无冲突退出 0 并写目标标记；有冲突退出 2，目标标记暂存在 `.leistd/` 下的 template.pending.json，没有未合并路径、改动的文件里也没有冲突标记时 `-Complete` 才写入。无标记的存量项目用 `-Adopt` 从某个提交的模板源码一次性接入，基线须与项目生成那次提交（`-BaselineCommit`，缺省为唯一根提交）一致（差异逐项确认），项目改过的规范基线（生成项目的 docs/standards）迁到 docs/project 下同名文件，只写目标包版本的标记。
- **升级闸门** `pwsh template-package/test-template-upgrade.ps1 -PreviousVersion <上一版> -CandidatePackage <候选 nupkg>`：从第二个模板版本起是模板发布的前提，只在发布时运行（[模板包发布](../framework/versioning.md#模板包发布template-通道)）。上一版在仓库之外生成 `identity-all-features` 与 `resource-capabilities-03`，提交项目定制（新增 npm 依赖，纯 API 为 NuGet 依赖；一处源码修改；替换一个图片，纯 API 没有图片时不适用），定制只放在上一版与候选之间稳定的位置；再用生成项目自带的同步脚本同步到候选，三类分开验收：无冲突自动合并、与候选重建的差异恰好是定制文件、verify 通过；在候选改动的同一行另做修改时退出 2、该文件是唯一的未合并路径且保留双方内容、标记未更新，未解决或只 `git add` 而留着冲突标记时 `-Complete` 被拒绝；按候选一侧解决并 `git add` 后 `-Complete`、verify 通过。`-LocalFeedPath` 给出候选 Framework 包源时 verify 只用这批包。
- **同步本地验收** `pwsh template-package/test-template-sync.ps1`：把 `template/` 副本打成 0.1.0、0.1.1（改一行、换图片、删文件、加文档、改依赖清单与锁文件）、0.1.2（只改文件名大小写）与 0.2.0（参数改名）几个本地包，不改动模板源码；跑升级闸门，再跑诊断反例（工作区不干净、缺少标记、源码生成、参数不兼容、已是目标版本、没有待完成同步时的 `-Complete`、`-ProjectRoot` 不是工作树根目录）、二进制与删除/修改冲突在 git 里的解决与 `-Complete`、新增路径上有被忽略的本地文件、只改大小写的改名，以及从首个模板包之前的真实提交存量接入、再同步到下一版。改同步脚本、标记库或闸门时运行，verify 缺省 `-Fast`。
- **SDK**：模板不带 `global.json`，生成项目用任意 .NET 10 SDK 即可还原和构建，实际使用的 SDK 记录在标记里。仓库内生成到 `.tmp/` 下的项目会继承仓库根 `global.json`，与真实用户不同；模板包验证因此在仓库之外生成。

## 前端依赖维护

默认与本地化载荷的 Angular 运行时、CDK、编译器和构建工具须按官方 peer 范围配套更新，并同时更新两套锁文件。先在隔离副本验证安装、原审计阈值、lint、构建与真实浏览器测试，再同步依赖文件；完整条件矩阵由 CI 执行，不能因依赖改动免除。不得用 `--force`、`--legacy-peer-deps` 或降低审计阈值绕过依赖冲突与安全门禁。

审计口径：门槛是生产依赖 high/critical 为 0（`npm audit --omit=dev --audit-level=high`），矩阵、生成项目 CI 的审计作业（GitHub 在 `verify` 作业内的审计步骤、GitLab 的 `frontend-audit`）与 testing.md 提示的本地命令同口径，不是全部 severity 总数为 0。生成项目的 `verify` 不含审计，CI 只在前端锁文件变化、定时和手动时运行；发布放行的审计责任在矩阵：按实际生成的前端锁文件（默认与本地化两套）各审一次，不按场景重复，网络失败有限重试，非零退出即放行失败。生产审计通过不代表工具链或部署产物没有漏洞。

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

浏览器认证变更在本地做目标测试与浏览器验证，PR 的增量场景由 PR CI、全部登记场景由合入后的 `full` 档承担。修改令牌寿命、刷新或到期判据时，本地用 `FakeTimeProvider` 或显式时刻在单元、集成测试里验证判据；真实到期边界（`test-template-oidc-e2e.ps1 -IncludeExpiryWait`）由 `full` 档承担，PR CI 默认省略真实到期等待，PR 成功不代表到期边界已验证，合入后须核对 full 的结果。真实官方处理器、Cookie 解保护、服务端票据删除、并发刷新、资源归属与可编译变异的边界见[浏览器认证维护规则](browser-authentication.md)。
