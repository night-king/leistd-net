# CI 质量计划与放行

本文定义 CI 的输入分类、作业划分、同候选计划、回执与放行责任，维护 `ci.yml`、`release.yml`、质量计划器或闸门脚本时读取，不随 NuGet 分发。

- 日常开发的本地验证见[质量检查与本地验证](./quality-assurance.md)。
- 模板矩阵的场景、档位、分片、PR 场景选择与回执见[模板质量验证](../template/quality-assurance.md#模板矩阵)。
- 发布通道与版本见[版本与发布规范](./versioning.md)。

## PR 的内部文档例外

文档与代码使用[计划器](../../scripts/plan-quality-checks.py)的唯一输入分类，读取完整 base→候选差异；删除和跨边界移动计入两端。

- 未知路径、空差异、force push、无法确认依赖或基准时，直接 PR 跑[代表场景冒烟](../template/quality-assurance.md#pr-增量保证)，其他触发方式完整验证。
- 直接 PR 可按责任缩小；手动、定时和实际发布候选保留完整责任；stable 同树复用按下述规则承接代码证据。

| 文档输入 | 必过责任 |
| --- | --- |
| 根 README、根 docs、仓库 Skill 的已登记 Markdown | Linux/Windows 完整静态检查、计划与汇总 |
| Template Markdown、项目 Skill 与 AI 入口 | 上述检查，加全部有效形态及 Ci 变体的实际生成、文档引用/锚点、模板残留、AI 入口、Skill 元数据及生成前端 Markdown 格式 |
| 实际随包 Markdown（包括 framework/NuGet.md） | 候选打包、全包内容与源码 Markdown 字节一致、XML 文档、文档示例编译及反例；空消费清单不跳过示例 |
| frontend-spartan.md 登记表 | 生成文档加 frontend-gates，验证实际组件与登记一致 |

扩展名不能代替输入依赖。

- 前端闸门读取 UI 组件、components.json、两份前端 package/lock、angular.json、Prettier/ESLint 配置与规则、模板配置、两个前端闸门脚本及 Spartan 登记表；这些输入变化必须保留该闸门。
- 纯后端与无关内部说明可以跳过。
- 文档与代码同改取责任并集，文档不把已证明的局部代码变更扩大到完整阶段。

不使用 workflow 级 paths-ignore。汇总严格核对每项 success/skipped、候选、计划与适用证明；检查数为零、失败、取消、意外跳过或缺失回执均失败。格式直接读取生成 frontend 文件，使用锁定 Prettier 与实际配置/忽略范围，避免 .tmp 被 Git 忽略后假绿；不扩展到根 docs。

非发布 push 只有同时满足下列条件，才允许轻量文档责任：

- before 是候选祖先；
- 同一分支 release workflow 的最新 before 推送运行和尝试成功，且质量汇总成功。

candidate 与 CI 分别查询 Actions API；失败、取消、进行中、缺失或 API/权限错误均完整验证。机器人或 [skip ci] 的 before 没有成功质量汇总，也按完整验证处理。发布基线另见[版本规范](./versioning.md#正式版发布全自动)。状态查询使用 GitHub 的[工作流运行接口](https://docs.github.com/en/rest/actions/workflow-runs#list-workflow-runs-for-a-workflow)和[指定运行尝试的作业接口](https://docs.github.com/en/rest/actions/workflow-jobs#list-jobs-for-a-workflow-run-attempt)。

### develop 与正式版内容相同

`release.yml` 的 candidate 对 develop push 先用本地 Git 取全局最高数值版本的 `vX.Y.Z`，再验证该 tag 是候选祖先且两者树相同。命中时冻结 `same_as_stable=true`，明确不发布，并整体跳过 quality；摘要记录 tag、提交与树。普通“不需要发布”的推送仍执行质量验证，PR、main 与手动运行不适用此例外。

树相同只证明没有新的仓库内容；不代表原 stable 的最终产物步骤已全部完成，部分发布仍按原运行恢复。tag 缺失、非祖先、树不同或比较无法完成时继续原计划；不生成空质量计划，不伪造质量成功，也不改变 stable 的同树复用和发布放行责任。

## 编译器、分析器与静态闸门

编译器和官方分析器能完整表达的规则优先在构建中执行，通过 `.editorconfig` 与 Release 的 `TreatWarningsAsErrors` 确保诊断会失败。例如公共 XML 缺失由 CS1591、命名空间由 IDE0130、无用 using 由 IDE0005 承担，不再建同义脚本。

自写闸门只承担它们不能表达的仓库语义：包/家族边界、条件模板的全集求值、随包文档与源码关系、词条/动作码、替换点或精确豁免。判断是否等价须同时核对输入范围、正反例、豁免、退出码和未执行分支；同名诊断或一次构建通过不算等价。官方机制放行而仓库禁止的语义须保留窄规则，不能为了减少闸门而放宽契约。

闸门清单唯一入口是 `scripts/check-all.ps1`，数量不在其他文档或 workflow 另行维护。

- 检查器的正反例自检与生产输入检查保护不同对象，不能互相替代。
- 优化可合并同一输入的重复遍历或索引构建，但仍须执行原自检与完整扫描；不建立没有实测收益的跨 run 索引缓存。
- API 漂移入口在同一进程中先执行全部正反例，再以本次源码索引扫描正文；规则失效立即失败，不提供只自检后提前退出的模式。

文档引用由 `scripts/check-doc-references.py` 单一实现：check-all 跑源码模式（路径与链接），矩阵文档检查以 `--root` 在生成产物上跑生成模式（另查 npm/ng/脚本命令）；豁免只登记在脚本内的精确白名单。

测试诊断消息由 `template/scripts/check-test-diagnostics.py` 单一实现并随模板分发：

- 生成项目 `verify` 跑项目模式；check-all 跑自检与 `--repository`（模板源测试加 `framework/tests`）；矩阵对每个生成产品以 `python -I` 运行，不依赖 `verify` 路径，并证明脚本不引用仓库其他文件。
- 判据只覆盖直接写出的消息参数与测试中 `throw` 的消息，不做类型推断或间接数据流分析，也不设豁免表。
- 维护工具与闸门脚本（`scripts/*.mjs`、`*.py`、`*.ps1`）面向人的输出不受测试诊断英文规则约束；脚本之间的判定只经退出码、回执字段与稳定标识（如矩阵回执、`verify -List` 的步骤名）传递，不匹配展示文字。

## CI 划分与分片

先测作业的运行、队列和依赖，按 DAG 关键路径决定优化顺序，不把所有作业节省的秒数相加当作墙钟收益。独立的包消费、框架契约与真实服务闭环可在同一候选 SHA 上并行，但质量结果须包含它们；拆成独立作业不能变成可选检查。

模板场景的档位、执行组与 full 分组依据见[模板矩阵](../template/quality-assurance.md#模板矩阵)。

PR 墙钟中位 ≤ 510 秒、各路径总交付成本中位低于基线的目标，按[成本观测](#成本观测)在切换后观测判定，不能以功能验收或单次耗时认定达标。

`quality-plan` 独立 checkout 固定候选与完整历史，输出 Version 4 的输入分类、Jobs、档位/模式、场景/容器及两份基线；被 `release.yml` 复用时以调用方传入的同一候选与基线计算，放行只认汇总输出的计划。framework-pack 按责任只打包一次，不可变 artifact 供消费者下载；不适用时连 SDK 安装一起跳过。每个作业直接检查计划中的自身责任，汇总始终运行。规划作业的新增 runner 时间计入净成本。

### 正式发布复用同树质量证据

仅 stable 的 main push 发布候选可复用其第二父提交的 develop push 运行；两树必须相同，且该 head 的最新 release.yml 运行及最新 attempt 已成功。源码、workflow 与工具解析策略相同即可，接受该策略允许的实际环境漂移；不承诺实际 SDK、Action 或 runner 字节相同。

计划器经 Actions API 读取完整作业列表及固定 ID 的 `quality-plan-<run_id>-<attempt>` 构件。来源必须是当场执行的 Version 4 full 计划，覆盖全部登记场景、执行组、两个容器、全部框架测试与消费者、包/生成文档和 PG/OIDC；逐项作业与受信汇总均成功。full 的标签或单独一个汇总绿灯不能替代责任核验，不跨 attempt 拼接，不复用其他运行的复用结果。构件缺失、过期、重复、归属不符、API/权限/分页/超时失败均带原因回退原 full 计划，旧运行不从日志补证据。

本次 `CandidateSha` 和发布基线不变；`QualityExecution=reuse`、`QualityReuse` 记录 RunId/Attempt/HeadSha/Tree/PlanArtifactId，Jobs、场景和容器责任保持完整。决定在计划作业完成远端核验后冻结；汇总核对被复用作业实际 skipped、见证匹配第二父提交和树，成功后才输出 `QualityValidated=true`。原始 current 分支继续逐项核对全部回执；错误或损坏的复用汇总失败关闭，重跑重新规划。

前端依赖审计依赖实时漏洞库，stable 每次仍执行且须成功；最终 Framework/模板包的打包、隔离消费、来源证明、nuget.org 还原与升级闸门也保持现场执行，不复用包产物。两种 Release 正文均记录复用来源。beta、PR 与手动 stable 的验证责任不变；develop push 与最高正式版同树的例外见[同内容判据](#develop-与正式版内容相同)。

### 成本观测

成本只读 GitHub API、按"每次合入"重建：PR 的全部 ci.yml 运行与每个 attempt，加合并提交上 release.yml 的 push 运行，逐项列出 run ID 与 attempt。总交付成本是全部作业运行时长之和，纯质量成本只算质量作业；按完整、前端、后端、框架、文档路径分别统计，失败、取消和重跑都计入。每类至少 5 次合入才给中位结论，样本不足时不宣称达标。切换后观测 7 天，与切换前的结果比较：PR 墙钟中位 ≤ 510 秒，且各路径总交付成本中位低于基线。

## 同候选输入计划

选择与发布输入分类唯一实现为 `scripts/plan-quality-checks.py`。

- 直接 PR 的计划一律是 `core` 档：完整 base→候选差异非空且全部输入都已建模时按选择器收窄（`ScenarioSelection=selector`），否则是代表场景冒烟（`representative`）；文档责任另取并集。
- 其他触发方式的计划都是 `full`（`all`），没有场景的文档计划记为 `none`。
- 场景的选择规则见[PR 增量保证](../template/quality-assurance.md#pr-增量保证)。

| 输入 | Framework 测试 | 包内容/隔离消费 | 模板场景与阶段 | 形态生成 | 独立 PG/OIDC |
| --- | --- | --- | --- | --- | --- |
| 仅模板前端 src/public/_mock 的支持文件 | 不适用，输入未变 | 不适用，框架输入未变；同改随包文档时按文档责任执行 | 选择器给出的场景；生成/形态、npm ci、healthcheck、lint、build、spec 发现与真实 Chromium | 执行 | 不适用 |
| 仅模板 backend/src 或 tests 的 C# | 不适用，输入未变 | 不适用，框架输入未变；同改随包文档时按文档责任执行 | 选择器给出的场景；生成/形态、restore/build、真实运行时、后端单元/集成 | 执行 | 命中登记路径才执行 |
| 前后端同改 | 不适用 | 同上 | 选择器给出的场景，完整阶段 | 执行 | 命中登记路径才执行 |
| Framework 组件/DDD/测试项目内的文件 | 改动项目反向依赖闭包内的测试项目 | 全部内容；该包与候选 nuspec 反向传递依赖的隔离消费 | `core` 全部场景完整阶段（下游冒烟） | 不执行 | 命中登记的框架家族才执行 |
| `template.json`、共享配置/依赖、脚本/workflow、未知输入、无效基线 | 全量 | 全部内容与全部消费 | `core` 全部场景完整阶段，加可证明的条件分支补选；适用容器 | 执行 | 命中登记路径才执行；基线无法确定时执行 |
| 手动/定时/发布 | 全量 | 全部内容与全部消费 | `full` 全部场景/阶段 | 执行 | 执行 |

PostgreSQL/OIDC 的登记路径在计划器里，PR 只在命中时运行，否则计划记为不适用，由放行前的 full 承担：

- `POSTGRESQL_INPUT`：持久化、迁移、租户、DbMigrator、DataProtection 与 data、multi-tenancy、unit-of-work、DDD Infrastructure 家族，以及各家族的 `*.EntityFrameworkCore` 项目；
- `OIDC_INPUT`：认证、令牌、Connect、OpenIddict、开放应用、Security、会话、服务调用与租户，以及 authorization、security、service-client、multi-tenancy 家族。

包内容的输入闭包不含模板源码，已证明仅改模板前端或后端时不重复核对基线已验证的包内容；候选 Framework 仍重新打包，供场景与端到端从同一候选 feed 消费。随包文档同改时取责任并集，保留包内容验证。

前后端各自省略的阶段要求实际生成输入保持不变。前端生产依赖审计不在场景片里，由独立的"前端依赖审计"作业执行（`scripts/audit-frontend-dependencies.ps1`）：两套锁文件各生成一次条件全开的最大闭包，核对它覆盖模板锁文件的全部依赖块，门槛 high/critical 为 0，只对网络类错误重试。计划字段 `DependencyAudit` 决定是否运行：stable 与手动运行恒执行（计划校验强制 stable 为真）；其余候选只在依赖闭包或审计本身的输入有改动（前端根下非源码文件、`template.json`、计划器、计划契约、审计脚本、`ci.yml`/`release.yml`）或无法证明输入差异时执行。依赖未变但漏洞库新增的公告不由 PR 承担，由 `scheduled-checks.yml` 每个工作日对 develop 全量审计发现；同一 workflow 扫描 NuGet 依赖（构建时 NU1900–NU1904 只告警，不阻断）并以 verify Fast 运行模板升级闸门。

Framework 依赖闭包只裁剪各自含单一 PackageReference 的空 restore/build 消费项目；全包内容、源码包集、DLL/XML/文档、重复包、缺失候选依赖仍先核对。它不裁剪 Framework 用例、DI/反射/配置语义、模板或服务闭环；出现显式跨项目编译输入时回退全量。人工 `-PackageIds` 保持已有局部入口，CI 使用独立计划并仍要求完整候选 feed。

### Framework 测试清单

`FrameworkTestProjects` 由 `framework/**/*.csproj` 的 `ProjectReference` 图求出：改动文件映射到所属项目，取反向依赖闭包中的 `*.Tests` 项目，与计划的 BaseSha/CandidateSha 绑定（`FrameworkTestSelection` 为 `affected`、`all` 或 `none`）。

以下情形取全集：

- 共享输入：`framework/*.props`、`framework/build/**`、`framework/tests/Directory.Build.props`、`framework/tests/shared/**` 共享测试基座、根构建输入与 `VERSION`；
- 文件不属于任何项目，项目或引用增删改名，引用指向不存在的项目；
- 无法解析的属性或 `Import`、未登记的 props/targets、链接文件或显式跨项目编译输入；
- 选择为空、不确定基线与脏树。

`test` 作业按清单逐项目运行，每个通过的项目写一份回执；汇总核对回执与清单一一对应，缺失、多出、重复、失败或候选/基线不符均失败，全集清单还须等于候选中登记的全部测试项目。

### 汇总与回执

汇总 `template-matrix` 使用 always；current 执行要求静态与独立 quality-plan 作业成功，按计划严格核对每个动态作业的 success/skipped，再按 quality-plan 输出的 Version 4 计划核对准确执行组、场景、阶段、SHA、档位、包与生成文档证明。

- 每个必要场景必须唯一分配；回执不能自行缩小范围。
- 登记 `Verify` 的场景在完整模式须回执 `Verify=pass`，其余为 `not-run`。
- 省略阶段写 `not-applicable`，不能用 skipped/pass 冒充执行；失败、取消、意外跳过、缺片、重复、跨组移动、错版本／SHA／档位／阶段及缺少容器责任全部拒绝。
- 矩阵和检查器共享计划验证入口 `quality-validation-plan.ps1`；没有计划的人工入口使用原逻辑组，不能拿 CI 执行组回执代替。
- 生成目录、数据库、feed/hive、包解包缓存和端口仍隔离，不共享可变产物。

### 本地查看计划

计划可在本地生成以检查选择结果：`python scripts/plan-quality-checks.py --tier core --event pull_request --base <完整SHA> --output .tmp/quality-plan.json`；容器适用时增加 `--container-smoke true`。这只是查看计划，按[本地不做](./quality-assurance.md#本地不做)不据此在本地执行 `core` 档，复现 CI 失败只跑失败的场景。

- CI 给矩阵传计划自身的档位（`-Tier <计划的 Tier> -ValidationPlanPath <计划>`），`-Slice` 必须取计划中的 key。
- 矩阵不带计划时不自动推测 base，按完整阶段执行。
- 不能将计划与手动跳过、`-Scenarios` 或手选容器场景混用，full 运行阶段不接受裁剪计划；已验证基线的非发布文档推送仅执行登记文档责任。

修改选择规则或回执时运行 `python scripts/test-quality-validation-plan.py`：它实际生成全部登记产品做前后对照，核对省略阶段的输入、改动输出都由选中场景生成，并运行选择器反例与错误回执；这是维护回归入口，不加入每日静态闸门。

### 范围裁剪与源码预检

范围裁剪必须基于 PR base/merge-base 到 head 的完整差异及实际依赖，不用单一 `HEAD^` 代替多提交 PR。无法确定范围时，直接 PR 跑代表场景冒烟、框架测试与消费取全集，其他触发方式全量执行；随包文档、props、lock、脚本与 workflow 都是质量输入。只有接替责任和变异证据完整时才削减组合入口的重复工作。

独立 `test-template-matrix.ps1` 默认先执行 symbols、using-guards、async-boundaries 三项源码预检，再打包、生成；矩阵不审计依赖，需要时单独运行 `pwsh scripts/audit-frontend-dependencies.ps1`。预检职责不由生成编译替代。

- 同一 CI 候选的源码预检由 `docs-sync` 完整运行 `check-all.ps1` 承担，矩阵传入 `-SkipSourcePreflight` 省略重复扫描，并核对总入口仍登记了这三个预检入口。
- 所有作业显式 checkout 相同候选 SHA；生成可与静态检查并行。
- 必过汇总必须等待并按计划核对范围、静态、打包、Framework 测试、包消费、PostgreSQL、OIDC 与模板分片的成功或不适用，再核对本档完整回执。
- 独立人工矩阵默认在生成前预检，不使用该 CI 开关。清单存在不能代替缺陷注入和失败传播验证。

## 放行责任

放行候选（develop beta、main 正式版与手动发布，即计划 `ReleaseRequired=true`）在发布前必须证明下表每一项；本表是放行责任的唯一出处，其他文档只链接这里。计划器对放行候选无条件设置 `ContainerSmoke=true`，不论输入分类；计划验证拒绝放行计划中任何作业不适用（含 PostgreSQL 与 OIDC）或缺少容器责任。汇总 `template-matrix` 逐项核对，任一项缺失、失败、取消或计数为 0 都以非零退出。同树复用按[上述规则](#正式发布复用同树质量证据)承接这些代码责任，本次实时依赖审计仍须成功：

| 责任 | 判据 | 汇总核对 |
| --- | --- | --- |
| full 全部登记场景 | 所有场景 Backend（含后端单元与集成测试）与 Runtime 为 pass；有前端的场景 Lint/Frontend/Test（Test 为前端测试）为 pass，且真实 Chromium 执行的用例数 > 0；无前端场景这三项为 not-applicable；登记 Verify 的本地身份场景另执行 Playwright 端到端且用例数 > 0；两步验证与确认身份的浏览器主路径在这批用例里，仓库不另设浏览器闭环 | `check-template-matrix-results.ps1`：回执 `BrowserTests` 取自 Vitest 汇总行，Test 为 pass 时须 > 0，否则须为 0；`E2ETests` 取自 Playwright 汇总行，是最终通过的用例数（passed 加重试后通过的 flaky，每个用例计一次），本地身份场景 Verify 为 pass 时须 > 0，否则须为 0 |
| 两个容器场景 | `$ContainerScenarios`（`standalone-external-login`、`resource-capabilities-03`）都执行容器阶段且为 pass | 同上，`Container=pass`，缺少场景结果同样拒绝 |
| PG 端到端、OIDC HTTP 端到端 | 作业为 success，且执行的断言数 > 0 | 作业状态；`check-quality-proof.ps1 -Kind postgresql/oidc` 核对 `e2e-proof.json` 的 `Assertions` |
| 生成证明 | 320 个形态、768 组互不相同的原始输入映射到这 320 个形态、三种角色的等价比对、4 个 Ci 变体、全部 `verify.ps1` 变体的步骤与失败传播 | `check-quality-proof.ps1 -Kind generation` 核对四片 `generation-results.json`：同一候选与源码摘要（与检出的模板源码逐文件比对）、形态无缺号无重复、对照片各项齐全，每个形态的 `verify.ps1` 键都被对照片核对过 |
| 框架 | 全部测试项目都有回执；包内容检查与隔离消费检查通过 | 全集清单等于候选登记的全部测试项目并逐项有回执；`-Kind package` 要求全部包与全部消费者 |

反例夹具在 `scripts/test-quality-validation-plan.py`（放行计划、回执与证明）和 `scripts/test-workflow-change-scope.py`（汇总作业状态与证明步骤接线）。

## 效率证据

验收先区分工作量变化：

- docs-only 按真实 PR 的路径判定、必要作业和跳过责任验收；
- 同候选去重按被删除的命令、接替缺陷检测及新增命令/依赖的净成本验收。

两者都完整披露实跑墙钟和 runner 合计，新增可归因成本吞掉节省或拖长关键路径时应修正。只有重排相同工作量的调度才使用固定配对的墙钟与 runner 联合门槛，不能把小幅去重收益套入高噪声重排实验。

声称定量性能改善或稳定预算时，使用同输入、同机器或 runner 规格、相同入口与明确的缓存条件，前后各至少三轮（符合同一比较条件的首轮可计入），报告全部值和中位数。

- 本机共享负载波动大时，用前后交替的成对测量，并以产物哈希确认两种变体确实不同；调度类改动以真实 CI 运行验收，成本按[成本观测](#成本观测)判定。
- 含 build 与 `--no-build`、TRX 方法时间和入口墙钟、不同 SHA 的历史 CI、并行阶段不能混算。首轮不清缓存时不称为完全冷启动；跳过用例不称为已经执行。
- 新调度模型与实际执行结果分开记录，实际关键路径变化后重新测量。调度改善与 L2 墙钟预算分别判定。
- 组数不设固定上限：新增分片按各场景实测耗时区间分配，并计入每片的准备成本（见[模板矩阵的执行组与分组依据](../template/quality-assurance.md#执行组与分组依据)），不放宽测试。
- 同候选阶段去重先核对工具实际输入、等价组和净收益；无合格项不建设缓存或来源证明系统。

依赖缓存：setup-node 的 npm 缓存；第三方 NuGet 包的 HTTP 下载缓存（`~/.local/share/NuGet/http-cache`，键为全部 `Directory.Packages.props` 与锁文件的哈希），用于矩阵、框架测试、包消费与 PostgreSQL/OIDC 作业。

- 缓存恢复后与保存前都删除其中的 `leistd.*` 条目；Leistd 包只经 packageSourceMapping 从候选包源还原，矩阵的解包目录仍按 run 隔离，候选包的版本与来源断言不受缓存影响。
- 单测用 Chromium 由 `scripts/install-test-chromium.ps1` 安装。版本直接读模板的两份锁文件，不再为读版本先生成工具项目。`--with-deps` 下载的系统依赖 .deb 用 `actions/cache` 缓存，键为 runner 镜像（`ImageOS`、`ImageVersion`）加 Playwright 版本。
  - 依据：2026-10 最近约 40 轮 CI 共 118 次执行，这一步中位 36 秒、P90 121 秒、最长 870 秒，超过 90 秒的 13 次。长尾来自 Ubuntu apt 镜像下载（单次 26 MB 用时 7 分 49 秒，曾让作业撞上 40 分钟上限）；浏览器本体从 Playwright CDN 下载约 6 秒。
  - 浏览器本体仍不缓存：Playwright 官方不推荐，恢复与下载耗时相当。不去掉 `--with-deps`：runner 镜像没有中日文字体，量宽与断行断言依赖它们。
  - 效果按上文的测量要求在合入后核对，前后各至少三轮。
- Docker 镜像不缓存：镜像拉取未单独测量，没有证据前不加。

CI 墙钟从本次尝试的起点到最后一个必要质量作业的 `completed_at` 计算，不用含收尾时间的 `updated_at`：初次执行用 `created_at`，重跑按各自 `run_started_at` 分别计时，不包含两次尝试之间的间隔。

- 每个作业分别记录启动偏移与 `completed_at - started_at` 的运行时间；启动偏移还包含依赖等待，不能全称 runner 排队。
- 沿依赖链记录前序结束到后序开始的间隔，不与启动偏移重复相加。工作流建立耗时与 runner 排队分开记录。
- 模型必须保留原输入与范围，若未计入建立、队列、artifact、汇总或容器成本，比较时须逐项对齐，不事后修改模型迁就实跑。

本地监听或容器访问受限时，经明确裁决可用同规格的完整远端 CI 验收；须核对实际日志、候选 head 与 PR merge 提交、全部必要作业和场景回执。单次历史前后对比只报告观测收益，不冒称三轮复测或稳定 SLA；同时变更的依赖、运行时代码与 runner 镜像版本须披露。浏览器下载、依赖准备与网络耗时有波动，分片收益不能全部归因于代码优化，也不能仅靠一次较快的准备阶段再增加分片。

官方依据：

- [NuGet 实际依赖解析](https://learn.microsoft.com/en-us/nuget/concepts/dependency-resolution)
- [微软测试分层与执行时机](https://learn.microsoft.com/en-us/dotnet/architecture/modern-web-apps-azure/test-asp-net-core-mvc-apps)
- [GitHub 矩阵与失败策略](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/run-job-variations)
- [作业依赖与 always](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#jobsjob_idneeds)
- [SDK 分析器](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/overview)
- [ASP.NET Core 测试层级](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0)
- [EF Core 测试选型](https://learn.microsoft.com/en-us/ef/core/testing/choosing-a-testing-strategy)
- [xUnit 并行配置](https://xunit.net/docs/config-xunit-runner-json)
