# 正式发布复用同一代码树的全量质量证据

状态：已完成 · 风险档：高（CI 与发布流程、放行责任） · 关联：https://github.com/zengqinglei/leistd-net/pull/174

## 1. 需求

### 问题与背景

`release.yml` 的 stable 通道（push `main`）每次都按 full 计划重跑全部质量责任：计划原因为 `Full: publication requires all quality responsibilities`。1.2.1 实测（run `40075013905`），质量阶段约 20 分钟，主要耗在 22 个模板场景的完整阶段，单片 10–19 分钟。

可是 main 的发布候选与它合并进来的 develop 提交，代码树完全一致：
- develop → main 用合并提交；发布后 main 合回 develop，main 总是 develop 的祖先，所以合并提交的树等于 develop 的树。
- develop 每次 push 都会跑一次 release.yml 的 beta 通道，其中已包含 full 质量计划。
- 1.2.0 的候选 `8aba28e5` 与 develop `f53a2d83` 同树；后者的 run `40016026301` 已经通过全部可复用的代码质量责任（22 个场景、2 个容器阶段、PostgreSQL 与 OIDC 端到端）。

证据按提交号认，所以同一份内容在正式发布时又全量跑了一遍。

**不是每次 develop 运行都具备完整责任**（Codex 阶段 1 核实）：非发布的 develop 推送，计划虽然是 `Tier=full`、`Mode=full`，却可能 `ContainerSmoke=false`、`DependencyAudit=false`。例如 1.2.1 的 develop run `40071176752`（只改了模板、没发框架 beta），容器阶段与前端依赖审计都没跑，所以不能作为复用来源。

`docs/specs/2026-10-18-ci-cd-ai-era.md` 第 293 行把这件事列为后置候选："main 复用 tree 相同的 develop 运行结果。启用前需要先拿出 main/develop 同 tree 的命中率与可省成本，并给出复用条件（run/attempt、受信 workflow、责任集合、工具链）以及不满足时重跑的路径。"本规格给出这些条件并实施。

**命中率与可省成本**（2026-10-24 实测）：

| 发布 | 候选 | 合入的 develop 提交 | 树 | develop 运行的容器阶段 |
| --- | --- | --- | --- | --- |
| v1.0.2 | `cc2b5d1a` | `3fc34061` | 相同 | 2/2 通过 |
| v1.1.0 | `1cf95ee6` | `e3f7cd00` | 相同 | 2/2 通过 |
| v1.1.1 | `deeee6fc` | `16b0c7f4` | 相同 | 2/2 通过 |
| v1.1.2 | `38e175cd` | `664cce6e` | 相同 | 2/2 通过 |
| v1.2.0 | `8aba28e5` | `f53a2d83` | 相同 | 2/2 通过 |
| v1.2.1 | `fbe877b8` | `634dbd01` | 相同 | 未安排（`ContainerSmoke=false`） |

- 树匹配率 6/6；按容器责任估算，可复用率约 5/6。
- 精确的可复用率要等计划能被持久读取之后才能逐次核对，历史运行没有保存计划构件。
- 每次命中可省的是可复用的代码质量作业（1.2.1 实测质量阶段约 20 分钟）。前端依赖审计、框架发布作业、模板发布作业照常运行。

用户 2026-10-24 确认按"AI 时代最佳实践终局"原则推进：验证跟着内容走，不跟着提交号走；每份内容只做一次全量验证；证据由流水线可验证地读取，不靠人或 AI 自证；条件不满足时回退全量。

### 复用的是验证结论，不是产物

最终发布的包版本号不受影响（用户 2026-10-24 提问后写明）：

- **质量阶段测的本来就是候选版本号的包。** 现行流程里，质量阶段打的是 `<版本>-ci.<run_id>.<attempt>` 的候选包（例如 1.2.1 的质量阶段打出 `Leistd.OperationRecords.Core.1.2.1-ci.40057762447.1.nupkg`），测试、生成与端到端都基于它。所以即使在 main 上重跑，被测包的版本号也不是最终版本。
- **最终版本号的包照常由发布作业现打现验，不复用：**
  - `release` 作业以最终版本打包，对整组最终 nupkg 做隔离消费、生成来源证明，再推送；
  - `template-release` 作业以最终版本打模板包，再做两项验证后推送：
    - 模板包验证（`test-template-package.ps1 -NuGetOrgRestore`）：包内容、隔离安装、来源标记、重建确定性，并要求生成项目从 nuget.org 还原最终框架版本；
    - 升级闸门（`test-template-upgrade.ps1 -Verify Full`）：用上一个已发布的模板版本生成两个代表场景，同步到本次最终版本的模板包。verify 用的框架包是本次源码打的候选版本 `-ci.<run_id>.<attempt>`。
  - 升级闸门只在 stable 发布时完整运行。develop 上只有 `scheduled-checks.yml` 每个工作日跑一次 verify Fast，所以它不是重复验证，不在复用范围内。1.2.1 实测 `template-release` 约 25 分钟，主要耗在这里。
- 版本号不是代码输入，只影响包元数据与包之间的依赖版本，这些正由上面两个作业在最终版本上验证。

| 验证层 | 对象 | 复用后 |
| --- | --- | --- |
| 代码行为（单测、场景、端到端） | 代码树 + 候选版本号的包 | 复用同一棵树在 develop 上的结果 |
| 最终发布物（元数据、依赖版本、隔离消费、nuget.org 还原、升级闸门） | 最终版本号的框架包与模板包 | 照常现跑 |

### 可复用与不可复用的责任

| 责任 | 是否按树复用 | 理由 |
| --- | --- | --- |
| 静态检查（Linux/Windows）、前端依赖闸门、候选包打包、框架单测、包消费、PostgreSQL 与 OIDC 端到端、生成四片、各场景执行组（含容器阶段）、矩阵汇总 | 复用 | 输入是源码与树内的工具配置 |
| 前端依赖审计（`DependencyAudit`） | **不复用，stable 每次现跑并要求成功** | 它访问外部漏洞库，树没变也可能新增公告；来源有没有跑过都不算数 |
| 框架发布、模板发布（含模板包验证与升级闸门） | 不复用 | 针对最终版本的发布物 |

### 目标与非目标

- 目标：
  - stable 通道的候选树，如果与某次已通过全量的受信运行的树完全一致，质量阶段就复用那次的证据，不重跑；
  - 不一致或证据不可验证时，照常跑全量。
- 非目标：
  - 不改 beta、nightly 通道（beta 本身就是全量证据的来源）；
  - 不改 PR 的增量计划；
  - 不改框架发布作业与模板发布作业：最终版本的打包、隔离消费、来源证明、升级闸门都依赖最终版本号，必须照常运行；
  - 不改 develop → main 的合并方式（仍是合并提交）；
  - 不引入外部缓存或来源证明系统。

### 功能要求

**复用条件**，全部满足才复用：
1. **同一棵树**：候选提交的树 SHA 等于被复用运行的 `head_sha` 的树 SHA。
2. **受信运行**：同一仓库、`path` 为 `.github/workflows/release.yml`、`event` 为 `push`、`head_branch` 为 `develop`、`status` 为 completed、`conclusion` 为 success；取该 head 最新一次 attempt。不接受 fork、PR、手动或定时触发的运行。
3. **责任集合完整**：被复用运行的计划包含全部可复用的代码质量责任，且这些责任在同一个 attempt 里全部成功。只看 `Tier`、`Mode` 或汇总作业成功都不够：1.2.1 的 develop 运行就是 full 档、汇总成功，却没有容器责任。具体判据见 §2。
6. **来源本身不是复用**：被复用的运行必须是当场执行的（`QualityExecution=current`），禁止递归复用。
4. **工具链**：树相同只证明源码、workflow、脚本与工具解析策略（`global.json`、锁文件、Action 主版本）相同，不证明实际工具链相同：`global.json` 是 `rollForward=latestFeature`，Action 与 runner 镜像按主版本浮动。本规格接受这些策略允许范围内的实际环境漂移。理由是复用来源与本次候选只相隔一次合并，最终产物另有现场验证，审计也现跑。不为此引入环境指纹或镜像固化。
5. **时效**：只复用合入 main 的那个 develop 提交（即候选合并提交的第二个父提交）的运行，不在历史里搜任意同树运行。

**回退**：任一条件不满足，或查询 Actions API 失败、超时、分页不完整、凭据缺失，都按现行方式跑全量，并在计划的 `Reason` 里写明未复用的原因。

**可追溯**：
- 复用时，计划记录被复用运行的 `RunId`、`Attempt`、`HeadSha`、`Tree`。
- 发布说明或 Release 正文写明"质量证据复用自 run X（develop `<sha>`，tree `<tree>`）"，以便审计。
- 放行判据（`release` 与 `template-release` 的 `if`）不得因为复用而放宽：仍要求 `Mode == 'full'` 且 `CandidateSha` 一致，并新增"复用证据已验证"的显式字段。

### 验收标准

| # | Given / When / Then | 覆盖 |
| --- | --- | --- |
| R1 | Given stable 候选与合入的 develop 提交同树，且其 release.yml 运行满足全部条件，When 计划，Then 可复用的代码质量作业不重跑，前端依赖审计照跑且须成功，计划带复用证据，`release` 与 `template-release` 照常放行 | 计划器夹具测试 + 汇总脚本夹具 |
| R1a | Given 同树、来源全部责任成功，但本次依赖审计失败，Then 不发布 | 夹具反例 |
| R2 | Given 树不同（如 main 上有未合回的提交），Then 跑全量 | 夹具反例 |
| R3 | Given 被复用运行失败、仍在进行、被取消、为较早的 attempt、来自其他 workflow 或分支、计划不是 full、缺少任一可复用责任（如 `ContainerSmoke=false`）、本身是复用运行，或计划构件缺失、过期、重复、归属不符，Then 跑全量，`Reason` 写明 | 夹具反例逐条 |
| R4 | Given Actions API 失败、超时、分页不完整、凭据缺失，Then 跑全量 | 夹具反例 |
| R5 | Given beta、nightly 通道与 PR，Then 行为与现在一致 | 现有计划器测试不改断言即通过 |
| R6 | Given 1.2.1 的真实数据（候选 `fbe877b8`，develop `634dbd01`，run `40071176752`），When 以只读方式查询真实 API 演练，Then 判定为**同树但责任不足**（无计划构件、缺容器责任），回退全量 | 真实 API 只读演练（反例） |
| R6a | Given 本 PR 合入 develop 后、首次 stable 之前，When 选一次具备全部可复用责任、带新计划构件的 develop 运行，用生产判定函数只读演练（候选合并拓扑在隔离的夹具里构造），Then 判定复用。"真实 API 的来源校验"与"模拟的候选上下文"分开记录；没有合格来源时 R6a 记为未验证，不能写通过 | 合入后只读演练，首次 stable 前完成 |
| R7 | Given 复用发生，Then Release 正文写明复用来源 | 发布说明生成的夹具测试 |
| R7a | Given 复用发生，Then `release` 仍以最终版本打包，并通过隔离消费与来源证明；`template-release` 仍以最终版本打模板包，并通过模板包验证与升级闸门。这些步骤的命令与顺序不变 | 接线断言；首次真实 stable 时核对日志 |
| R8 | Given workflow 与脚本改动，Then `scripts/test-workflow-change-scope.py`、`scripts/test-quality-validation-plan.py` 等自检通过 | 被改脚本的自检 |

### 方案

在计划器 `scripts/plan-quality-checks.py` 新增"同树全量证据"的查询，与现有的 `verified_quality_base` 采用相同的 Actions API 读取方式与校验口径：
- 只在 stable 通道且需要发布时调用；
- 命中时，计划仍为 `Mode == 'full'`，各质量作业标记为"复用"而不执行，汇总作业改为校验复用证据；
- 未命中时，计划不变。

`release.yml` 与 `ci.yml` 的放行与作业条件按新字段调整，不放宽现有判据。具体字段名、汇总作业如何校验复用证据、责任集合从哪里读取（被复用运行的计划输出、作业列表或构件），在 §2 定型。

## 2. 方案

设计取自 Codex 阶段 1 的建议（`.tmp/release-reuse/brief.md`），以下为定型结果。

### 2.1 证据载体：保存原计划，读取固定 attempt 的作业

- **计划构件**：`quality-plan` 作业把已经生成的 `.tmp/quality-plan.json` 原样上传为构件 `quality-plan-<run_id>-<attempt>`，不另建责任清单。
  - 读取时，经官方 artifact API 按 ID 下载，并核对构件的 run、attempt 与 head 归属；不取"最新同名构件"。
  - 过期、缺失、重复、下载失败一律回退。
  - 历史运行没有这个构件，按缺证据回退；不维护"从日志里猜计划"的兼容路径。
- **来源计划的判据**：
  - 受支持的契约版本、来源的 `CandidateSha`；
  - `Tier=full`、`Mode=full`、`ScenarioSelection=all`，场景与执行组完整；
  - `ContainerSmoke=true` 且两个容器分配齐全；
  - `FrameworkTestSelection=all`、`ConsumerProjects=null`，生成文档与包文档责任完整；
  - 可复用的 Jobs 责任全部为 true；
  - `QualityExecution=current`。
  - 不要求来源 `ReleaseRequired=true`：只要责任确实齐全，非发布的 develop 运行也可以作为来源。
- **作业结果**：用 `/runs/<id>/attempts/<attempt>/jobs` 读取固定 attempt，核对计划作业、各可复用作业，以及计划导出的每一个场景执行组和汇总都是 completed/success。作业名按当前 workflow 的稳定命名，由计划推导；缺失、重复、失败、取消、意外跳过都不接受，不跨 attempt 拼接。
- 同树的受信汇总在来源运行里已经做过浏览器与端到端计数、矩阵与包和生成证明的深入校验，所以"完整的 full 计划 + 逐项作业成功 + 汇总成功"即可承接这些证明，不重新实现原始检查器。

### 2.2 查询与冻结

- 只在 stable 通道、push `main`、实际需要发布时尝试。以下情况直接回退：候选不是合并提交、父提交不是两个、第二父提交没有受信的 develop push 运行、两树不同。手动触发的 stable 照常全量。
- 先确定第二父提交最新一次 release.yml push/develop 运行及其最新 attempt，再检验状态。不先按 success 过滤，不能跳过更新的失败或进行中运行去拿旧的成功。核对同仓库与 `head_repository`，分页完整。权限、网络、超时、JSON 异常都生成带具体原因的全量计划。
- 网络读取完成前，复核所选运行仍是该 attempt，并且 completed/success。之后冻结决定，由 `quality-plan` 输出，不允许调用方传入一个 `Verified=true`。
- 计划阶段的失败回退全量。作业已经按复用跳过之后，如果汇总发现计划损坏或本次审计失败，直接失败关闭，不在汇总里补跑；重跑时重新规划。汇总里不新增必须成功的远端查询。
- 与 `verified_quality_base` 共用同文件内的 Actions GET、超时、分页完整性、最新 run/attempt 选择与受信身份校验；两种语义的调用分开保留，不把前者的 `Verified` 当作复用证明。

### 2.3 计划契约与 DAG

- 计划契约版本升级，新增字段：
  - `QualityExecution`：`current` 或 `reuse`；
  - `QualityReuse`：`{Verified, RunId, Attempt, HeadSha, Tree, PlanArtifactId}`；
  - `QualityValidated`：只有汇总成功后才为 true。
  - 普通计划默认 `current`、`Verified=false`、`QualityValidated=false`。失败原因沿用现有的 `Reason`。
- 本次计划的 `CandidateSha` 始终是 main 候选；`Mode`、`Tier` 保持 full。场景、执行组、Jobs、测试与消费清单、`ContainerSmoke` 不得置空或置 false。来源的 develop SHA 只放在 `QualityReuse` 里。发布与模板基线按本次 main 独立计算，不复制来源的 `ReleaseRequired`、`TemplateRelease` 或 tag 决策。
- **`ci.yml`**：
  - `quality-plan` 始终执行。Linux/Windows 静态作业新增对它的依赖。
  - 可复用作业的 `if` 在原判据上加 `QualityExecution != 'reuse'`。
  - `dependency-audit` 保留现有判据，stable 仍需本次成功。
- **汇总 `template-matrix`**：保留完整的 needs 与 `if: always()`。
  - current 分支：保持全部原有检查。
  - reuse 分支：要求 `quality-plan` 成功、被复用作业确为 skipped、本次 `dependency-audit` 成功，并核对 `QualityReuse` 与本次候选的第二父提交和树一致。不把 skipped 当成 success；本次运行不存在的 feed 与回执下载、检查步骤全部跳过。
  - 输出经过汇总验证的**本次**计划。没有有效输出、契约版本未知、复用字段不全或相互矛盾时，一律不发布。
- **`release.yml`**：`release` 与 `template-release` 的 `if` 保留全部原有判据，再加 `QualityValidated`，以及 `QualityExecution == 'current' || (QualityExecution == 'reuse' && QualityReuse.Verified)`。不增加 `quality.result == 'skipped'` 一类的替代条件。打包、来源证明、nuget.org 还原与升级闸门不改。
- `quality-validation-plan.ps1` 及全部消费方同步新契约。如果共用验证器去校验来源计划，就增加显式的期望候选参数，默认仍严格等于当前 HEAD。

### 2.4 可追溯

框架发布说明与模板发布说明两处，消费汇总输出的计划，追加同一段短说明：来源 run 链接、attempt、develop 完整 SHA、tree，并注明"本次依赖审计与最终产物验证另行执行"。格式函数放在 `scripts/release-commits.ps1`，两处共用。

### 2.5 兼容与回滚

- 所有回退路径都等于现行的全量。合入后到第一份带构件的 develop 运行出现之前，stable 一律回退全量，行为与现在一致。
- 回滚：直接回退本 PR。计划构件只是多上传一个文件，不影响其他消费者。

## 3. 计划

一个 PR，目标 develop。

| # | 任务 | 产物 | 验证 |
| --- | --- | --- | --- |
| 1 | 计划器：同树证据查询（与 `verified_quality_base` 共用 GET 与校验）、来源计划判据、冻结、新契约字段 | `scripts/plan-quality-checks.py` | `scripts/test-quality-validation-plan.py` 夹具：R1–R5 的正例与全部反例 |
| 2 | `ci.yml`：上传计划构件、作业条件、汇总的 reuse 分支 | `.github/workflows/ci.yml` | 汇总脚本夹具：reuse 与 current 两条路径的放行与失败；复用时不下载本次不存在的 feed |
| 3 | `release.yml`：放行条件、发布说明 | `.github/workflows/release.yml`、`scripts/release-commits.ps1` | `scripts/test-workflow-change-scope.py`；发布说明有、无复用两种的夹具（R7）；打包等步骤的接线断言（R7a） |
| 4 | 计划验证器新契约 | `scripts/quality-validation-plan.ps1` | 现有测试改契约字段，行为断言不删 |
| 5 | 常驻规则 | `docs/framework/ci-release-gates.md`、`versioning.md`、`quality-assurance.md`，`docs/template/versioning.md` | 文档引用与锚点闸门 |
| 6 | R6 真实 API 只读反例演练 | 记录输出 | 真实 API，只读 |
| 7 | R6a 合入后、首次 stable 之前的只读正例演练 | 记录输出，填进 §4 | 真实 API 来源校验 + 隔离的候选上下文 |

**不做的事**：
- 不复用包产物；
- 不改 beta、nightly、PR 的验证行为，也不扩大 beta 的验证范围去提高命中率；
- 不改合并方式；
- 不引入环境指纹、外部缓存或来源证明系统；
- 不为了测试而触发发布 workflow 的手动入口。

## 4. 结果

### 实际交付

PR #174（合并提交 `89f9fc87`）：
- 计划器 Version 4：同树来源查询（与 `verified_quality_base` 共用 Actions 读取）、原计划构件下载与逐字段责任核对、固定 attempt 作业核对、冻结；
- `ci.yml`：上传计划构件、可复用作业按执行来源跳过、汇总的 current 与 reuse 两条路径；
- `release.yml`：两条放行条件追加 `QualityValidated` 与合法执行来源，发布说明写明复用来源；
- 计划验证器与发布说明格式函数；
- 四份常驻规则。

### 与计划的偏差

- 阶段 1 审查（Codex）推翻了"6/6 可复用"的前提：非发布的 develop 推送可能没有容器与审计责任。据此改为逐字段核对来源责任；依赖审计不复用、stable 现跑；工具链按"树内策略相同"表述；R6 改为反例，新增 R6a。
- 提交类型由 `feat(ci)` 改为 `ci:`：CI 内部改动不应让下一个正式版的次版本号加一。PR 开出前，修改提交信息后覆盖推送了尚未开 PR 的自有分支。

### 验收记录

| 项 | 结果 |
| --- | --- |
| R1–R5、R7、R7a、R8 | 计划器自检 566 个子进程检查（其中 363 个反例按预期非零退出），另有 70 个来源反例；workflow 接线断言通过，两个发布作业除发布说明外的 38 个步骤与原 workflow 逐项相同、顺序不变；PR CI 17 项通过 |
| R6（真实 API 反例） | 1.2.1：候选 `fbe877b8` 与 develop run `40071176752` 同树，但没有计划构件、没有容器责任，回退全量 |
| R6a（真实 API 正例） | 合入后的 develop run `40082252854` / attempt 1 / 计划构件 `12555632619`：来源校验通过，19 个作业成功，容器两片成功。在隔离克隆里构造的同树双父候选（第一父 `a888fc16`、第二父 `89f9fc87`）判定为复用，完整 stable 计划通过生产验证器；不同树的候选回退全量，计划也通过验证器 |

### 遗留项

- ~~首次复用的 stable 发布时实测并补记~~：已于 2026-10-24 在 1.2.2 正式发布中实测，见下节。
- 非发布的 develop 推送（如只改模板）没有容器责任，不能作为来源，那时 stable 会回退全量；这是规格接受的结果。

### 首次生产实测（2026-10-24，v1.2.2）

stable run `40089893621`（候选 `15c37013`，第二父 `3c20f953`，复用 develop run `40087492037` / attempt 1 / 计划构件 `quality-plan-40087492037-1`）：

| 指标 | v1.2.1（run `40075013905`，全量） | v1.2.2（复用） | 变化 |
| --- | --- | --- | --- |
| 质量阶段墙钟时间 | 20.8 分钟 | 1.4 分钟 | −93% |
| 质量阶段 runner 用量 | 114.7 分钟（20 个作业） | 1.1 分钟（计划、依赖审计、汇总 3 个作业） | −99% |
| 框架发布作业 | 6.1 分钟 | 5.9 分钟 | 不变 |
| 模板发布作业 | 24.6 分钟 | 25.3 分钟 | 不变 |
| 正式发布总墙钟时间 | 51.8 分钟 | 32.7 分钟 | −37% |

可复用作业全部为 skipped，依赖审计现跑并通过，汇总核验后放行；框架与模板的 Release 正文都写明了复用来源。

### 已上收的常驻文档

`docs/framework/ci-release-gates.md`、`docs/framework/versioning.md`、`docs/framework/quality-assurance.md`、`docs/template/versioning.md`。
