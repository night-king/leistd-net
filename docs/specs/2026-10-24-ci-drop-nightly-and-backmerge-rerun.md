# 删除 nightly 渠道，发布回合并不再重复验证与发布

状态：进行中 · 风险档：高（CI 与发布流程、发布渠道删除） · 关联：https://github.com/zengqinglei/leistd-net/pull/175

## 1. 需求

### 问题与背景

用户 2026-10-24 要求按"AI 时代最佳实践终局"审视整条 CI/CD，原则是：不过度复杂、不过度安全设计、符合官方推荐、不受现状约束。审视后确认以下两处是"同一份内容的重复运行"，有运行记录佐证。

**一、nightly 渠道在已经验证过的提交上重跑全量，产物又没有消费方。**
- `release.yml` 的 `schedule`（UTC 18:00，工作日）以 develop 的 HEAD 为候选，跑全量质量，然后把 `x.y.z-preview.<yyyyMMdd>.<run_number>` 推到 GitHub Packages。
- 实测：
  - 2026-10-23 的 nightly（run `40023447654`）在 `f53a2d83` 上跑了全量，用 114 runner 分钟；这个提交 1 小时前推送时，它的 push 运行刚刚完成全量；
  - 2026-10-21 的 nightly（run `39677313555`）同样如此，用 119 runner 分钟。
- 消费方：三个下游业务仓（abroker crm、identity、foundation）都没有配置 GitHub Packages 包源，它们跟进的是 nuget.org 上的 beta。
- develop 每次推送都会按需发布 beta，已经覆盖了预发布消费需求。随时间变化的外部检查（前端依赖全量审计、NuGet 漏洞扫描、模板升级闸门 verify Fast），由 `scheduled-checks.yml` 每个工作日承担。

**二、发布后的回合并在 develop 上重跑全量，还发布一个内容相同的 beta。**
- 正式发布在 main 上提交版本回写（`chore: 发布 vX.Y.Z [skip ci]`），随后经 PR 把 main 合回 develop。
- 实测：
  - 回合并 `21b1ca91` 的树 `e952eea1`，与正式发布回写提交 `a888fc16`（tag `v1.2.1`）的树完全相同；`77a42f18` 与 `601e8268`（`v1.2.0`）同样相同。
  - 两次回合并的 develop 运行（`40078491408`、`40062424960`）都跑了全量，各用 119、114 runner 分钟，并发布了 beta，例如 `1.2.1-beta.1`。它的打包输入（源码）与刚发布的正式版相同，只有版本号与包元数据不同。
- 树相同只证明**没有新的仓库内容**，也就不该产生新的发布物；它不证明那次正式发布的所有步骤都已成功（Codex 阶段 1 意见 3）。正式 tag 推送之后，模板发布作业可能还在运行或已经失败，那时按现有的"部分发布恢复"重跑原 stable 的模板作业，不由这次 develop 推送代替。

### 切换安排：nightly 什么时候真正停止

`schedule` 运行的是**默认分支（`main`）上的 workflow**（[GitHub 文档](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#schedule)），所以只在 develop 上删除停不掉它（Codex 阶段 1 意见 1）。main 上旧的 `release.yml` 会检出 develop，并把 `--release-channel nightly` 传给计划器；develop 删掉这个通道之后，旧的定时任务会在候选规划时失败。

- **完成口径**分两步：
  - 代码合入 develop：nightly 的代码删除完成；
  - 下一次 develop→main 正式发布（需用户另行授权）：默认分支的定时触发才真正删除，nightly 停止。
- **两步之间的窗口**：main 上的旧定时任务每个工作日会失败一次（UTC 18:00）。这些失败不发布任何东西，接受这个窗口，不为它保留兼容代码。
- **建议**：合入 develop 后尽快做一次正式发布。这一次发布同时也是 #174 同树复用在生产上的首次实测。

### 目标

1. 删除 nightly 渠道：去掉 `release.yml` 的 `schedule` 触发、nightly 通道及其全部分支，并同步计划器、测试、恢复摘要与常驻文档。
2. develop push 的候选树如果等于最近一个正式版 tag（`vX.Y.Z`）所在提交的树，判定为"自正式发布以来没有新内容"：不跑质量，也不发布 beta；作业摘要写明原因。

### 非目标

- 不删 `scheduled-checks.yml`，它是随时间变化的外部检查，不是重复运行：前端依赖全量审计、NuGet 漏洞扫描、模板升级闸门 verify Fast 都在这里，删除 nightly 不影响它们。只改其中提到 nightly 的注释。
- 依赖升级由 Dependabot（`.github/dependabot.yml`，NuGet 与 GitHub Actions，每周一次）承担，与 nightly 无关，不改。npm 依赖没有自动升级：模板的前端锁文件含条件行，Dependabot 解析不了。这是一个独立的缺口，不在本规格范围（用户 2026-10-24 提问时确认）。
- 不删已经发布的 preview 包与 tag：删除不可恢复，这些包也不影响任何人。
- 不改版本推算与 `VERSION` 回写机制，不改 develop → main 的合并方式。
- 不改升级闸门（仍是 verify Full）、不改它的候选框架包来源、不引入 merge queue：理由见"已确认的决策"。
- 不改 PR、stable、`workflow_dispatch` 的现有行为（#174 的同树复用保持不变）。

### 功能要求

**nightly 删除**：
- `release.yml` 删除 `schedule` 触发，删除 `channel=nightly` 的推算、版本形态、GitHub Packages 推送、`packages: write` 权限、只为 nightly 存在的分支与注释。stable Release Notes 的累积规则不再提 nightly。
- 计划器、计划契约里的 nightly 通道，以及测试里的 nightly 用例，一并删除。保留的通道是 `stable`、`beta`，加上 PR 与 `workflow_dispatch`。
- `write-release-recovery-summary.ps1` 删除 nightly 分支。
- 常驻文档 `docs/framework/versioning.md`、`ci-release-gates.md`、`docs/template/versioning.md`：
  - 删除 nightly 的行与"引用 nightly 包"一节；
  - 写明预发布消费用 nuget.org 上的 beta；
  - 写明随时间变化的检查由 `scheduled-checks.yml` 承担。
- 改契约前，全仓 `git grep -n -i nightly`（不加路径），确认没有遗漏的消费方。

**回合并不重复**：
- 只对 develop push 生效。判据：候选提交的树 SHA 等于最近一个正式版 tag 所在提交的树 SHA，tag 取按版本排序最高的 `vX.Y.Z`，且须为候选的祖先。
- 命中时：
  - `release.yml` 的 `candidate` 输出"不需要发布"，`quality` 不运行全量；
  - 作业摘要写明"候选树等于 vX.Y.Z（提交、tree），自正式发布以来没有新内容，不验证、不发布"；
  - 被 `quality` 跳过影响的下游作业不能因此失败或误发布。
- 不命中时：行为与现在完全相同。
- 判据只用本地 git，不需要 API。tag 不存在、不是祖先、或树不同，都按不命中处理。

### 验收标准

| # | Given / When / Then | 覆盖 |
| --- | --- | --- |
| N1 | Given 删除后的 `release.yml`，Then 不再有可执行的 nightly 发布渠道：没有 `schedule` 触发、没有 nightly 通道、没有 GitHub Packages 推送，`packages: write` 已移除。全仓 `git grep -n -i -E 'nightly|preview|github packages|夜间'` 的剩余匹配逐类解释为历史读取或无关业务语义，例如操作记录的 `nightly-cleanup` 示例保留不改 | 接线断言 + grep 记录与分类 |
| N1a | Given 历史 preview tag 仍然存在，Then 它仍不能成为 beta 的发布基线；`release_tag` 对 stable 与 beta 的正向匹配不变 | 保留 `test-workflow-change-scope.py` 的历史 preview tag 反例 |
| N2 | Given 计划器与测试，When 运行，Then 去掉 nightly 后的全部原有行为断言通过（stable、beta、PR、dispatch） | `test-quality-validation-plan.py`、`test-workflow-change-scope.py` |
| N3 | Given 恢复摘要脚本，Then stable、beta 的各阶段恢复指引不变 | 该脚本已有的自检或夹具 |
| B1 | Given develop push 的候选树等于 `v1.2.1` 提交的树（以 `21b1ca91` / `a888fc16` 为真实样本），When 计划，Then 判定"没有新内容"，不跑质量、不发布 | 计划器夹具 + 用真实提交的本地只读演练 |
| B2 | Given 树不同（例如回合并之后又合入了一个 PR），Then 行为与现在相同 | 夹具反例 |
| B3 | Given tag 不是候选的祖先，或没有正式版 tag，Then 按不命中处理 | 夹具反例 |
| B4 | Given 命中，Then `quality` 整体 skipped、`validation_plan` 为空时，`release` 与 `template-release` 的真实条件表达式都拒绝，没有 JSON 求值错误，整次运行不失败、不推送 | 用真实表达式的夹具 |
| B5 | Given 普通的"不需要发布"的 develop 推送（如只改文档或模板），且树不等于正式版 tag 的树，Then 质量照常运行 | 夹具反例 |
| B6 | Given PR、main push、`workflow_dispatch`，即使树等于正式版 tag 的树，Then 不适用本判据，行为不变；#174 的 current 与 reuse 路径不变 | 夹具反例 |
| B7 | Given 多个正式版 tag，Then 先按数值版本取全局最高的 `vX.Y.Z`，再验证它是候选的祖先；不先筛祖先、误用旧版本 | 夹具反例 |
| D1 | Given 常驻文档与现行说明，Then 文档引用、锚点闸门通过；nightly 的现行说明已删除，预发布与外部漂移检查的去向已写明。范围包括 `docs/framework/versioning.md`、`ci-release-gates.md`、`quality-assurance.md`（L3 表的"工作日夜间"）、`docs/template/versioning.md`、`ci.yml` 的"夜间"注释、`scheduled-checks.yml` 第 10 行的"在 nightly 预览之后"注释（只改注释，触发与检查不变；它自己打包候选框架，不依赖 nightly 产物）、`framework/common.props` 注释、`skills/leistd-net-framework/SKILL.md` 的 nightly 升级说明 | `check-all -Only` 相关闸门；Skill 改动按 `validate-skills.ps1` |

### 已确认的决策

- 用户 2026-10-24：按最佳实践终局、不过度复杂、不过度安全、官方推荐、不受现状约束的原则，由 Claude 决策。Claude 决定做以下两项，用户随后确认开始实施：
  1. 删除 nightly 渠道，不给它加复用：给一个没有消费方的渠道增加复杂度，不如删除；
  2. 回合并按"树等于最近正式版 tag 的树"判定为没有新内容。
- 不做的项及理由：
  - 升级闸门改 Fast：Fast 不含前端构建与测试，同步后的编译错误会漏掉；补齐需要定制组合，复杂度上升；它是唯一的升级路径验证，每次正式发布只跑一次；
  - 升级闸门改用最终框架包：收益约 1 分钟，最终包刚推送时 nuget.org 还没建好索引，要改成作业间传递构件；
  - merge queue 或在 develop 复用 PR 结果：PR 与 develop 的责任集合不同，收益与复杂度不成比例；
  - 版本号改为从 tag 推算、取消回写：`VERSION` 与模板版本字面量是刻意设计（生成项目自包含、可显式指定下一版），改动面过大，第 2 项已经解决了它的主要代价。
- 流程沿用此前做法：Codex 审方案直到明确通过，Codex 实施与验证，Claude 审代码后合入 develop。

### 方案

两项合成一个 PR：
- **nightly**：纯删除，同步全部消费方。
- **回合并**：见 §2。

预计每周省 700–800 runner 分钟：nightly 每个工作日约 115 分钟，回合并每次正式发布约 120 分钟。

本 PR 改了 workflow 与 `scripts/`，合入 develop 后会保守地发布一个框架 beta，这是现行规则，与本次改动无关。

## 2. 方案

### 2.1 nightly 删除

- `release.yml`：删除 `schedule` 触发，以及 nightly 的通道推算、版本形态、GitHub Packages 推送、`packages: write` 权限、只为 nightly 存在的分支与注释。`scheduled-checks.yml` 的触发与检查责任全部保留。
- 计划器、计划验证器、测试：删除 nightly 通道值与对应用例。保留"历史 preview tag 不能成为 beta 基线"的反例，因为旧 tag 仍在仓库里。
- `write-release-recovery-summary.ps1`：删除 nightly 分支。
- 现行说明：按 D1 列出的范围同步。

### 2.2 回合并：独立的"同正式版树"输出

- 在 `candidate` 作业里冻结一个独立输出，例如 `same_as_stable`。只对 develop push 计算：
  1. 按数值版本取全局最高的正式版 tag `vX.Y.Z`；
  2. 验证它是候选的祖先；
  3. 比较两者的树 SHA。

  全部满足时为 true，同时记录 tag、提交与 tree，用于摘要。只用本地 git，不查 API。
- 命中时：
  - `candidate.outputs.needed` 明确为 false；
  - `quality` 的 `if` 增加 `same_as_stable != 'true'`，整体跳过；
  - `release` 与 `template-release` 保留现有条件，不加任何 `always()` 一类的放行。`quality` 跳过时 `validation_plan` 为空，现有条件应当自然拒绝；B4 用真实表达式验证不会出现 JSON 求值错误，如果会出错，就在表达式里先判断 `needs.quality.result == 'success'`；
  - 作业摘要写一行："候选树等于 vX.Y.Z（提交 `<sha>`、tree `<tree>`），没有新的仓库内容，不验证、不发布。"不宣称那次正式发布的全部步骤已完成。
- 不命中时，计划与 DAG 与现在完全相同。
- 不新增 `QualityExecution` 的枚举值，不生成空的质量计划，不放宽计划验证器。

### 2.3 兼容与回滚

- 回滚：直接回退本 PR。nightly 的回滚要在 main 上同样回退才能恢复定时触发。
- 已经发布的 preview 包与 tag 不删除，也不影响任何消费方。

## 3. 计划

一个 PR，目标 develop。

| # | 任务 | 产物 | 验证 |
| --- | --- | --- | --- |
| 1 | 删除 nightly 渠道 | `release.yml`、计划器、计划验证器、恢复摘要脚本、测试 | N1、N1a、N2、N3；接线断言 |
| 2 | 同正式版树判据与调度 | `release.yml` 的 `candidate` 与条件；判据代码放在最简单的落点 | B1–B7：夹具与真实表达式；B1 用 `21b1ca91` / `v1.2.1`、`77a42f18` / `v1.2.0` 在本地只读演练 |
| 3 | 现行说明同步 | D1 列出的文档、注释与 Skill | 文档引用与锚点闸门；`validate-skills.ps1` |
| 4 | 合入后的切换 | 用户授权的下一次正式发布 | 首次正式发布后，确认默认分支已没有 `schedule`，并补记 #174 复用的实测耗时 |

**不做的事**：见 §1"非目标"。另外，不为切换窗口保留 nightly 的兼容代码。
