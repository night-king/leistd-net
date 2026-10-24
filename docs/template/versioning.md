# 模板版本与发布

模板打包为 `Leistd.Templates`（`template-package/Leistd.Templates.csproj`，`PackageType=Template`），由 `release.yml` 的 `template-release` 作业发布。版本与框架相同，tag 为 `template-vX.Y.Z`，包版本由包元数据承载。

提交规范、分支与 PR、需单独授权的发布操作与下游通知同框架，见[版本与发布规范](../framework/versioning.md)。包内容、来源标记、同步与升级闸门的验证见[模板包与来源标记](./quality-assurance.md#模板包与来源标记)。

## 版本与发布时机

模板与框架同号：`main` 上框架正式版与模板总是一起发布，模板版本就是本次框架版本，生成项目默认的 `LeistdFrameworkVersion` 也是它。下游升级只认一个版本号。

"模板有变化"指自最高的 `template-v*` tag（须为候选祖先）起，有提交改动 `template/` 或 `template-package/`；框架 stable 的版本回写提交（`chore: 发布 vX.Y.Z [skip ci]`）不计入。

| 情况（`main`） | 行为 |
| --- | --- |
| 框架与模板都有变化 | 两者同号发布 |
| 只有模板变化 | 框架也发布：包内容与上一版相同，发布说明列出模板提交 |
| 只有框架变化 | 模板也发布：内容只有默认框架版本变化，发布说明写明"模板无变更" |
| 都没有变化 | 都不发布 |
| 手动触发（workflow_dispatch） | 框架正式版照常发布，模板随之同号发布 |

其他触发：

- push `develop`：模板有变化时，只打包并跑包内容、隔离安装、来源标记与重建确定性的验证（版本为 `<VERSION>-ci.<run_id>.<attempt>`）；不做 nuget.org 还原、不推送、不打 tag。develop 上默认框架版本仍是上一个正式版，之后才首次发布的框架包还没有正式版，还原必然失败。
- nightly：没有模板通道。

版本幅度由框架通道推算，范围内的全部提交（含模板提交）参与：任一侧的破坏性变更都会让 1.x 之后的版本升 Major。主版本号因此表示"下游需要动手"，不只是框架 API 不兼容。模板的 `!` 同样必须带脚注。

## 放行条件

质量计划在 `main` 上的 `TemplateRelease.Required` 恒等于框架的 `ReleaseRequired`，模板有变化时两者都为真；计划不得收窄，适用与框架发布相同的全部[放行责任](../framework/ci-release-gates.md#放行责任)。

- `template-release` 作业还要求同一候选 SHA 的质量汇总成功，且框架通道已成功（develop 上可为跳过）。
- stable 可按[同树质量证据规则](../framework/ci-release-gates.md#正式发布复用同树质量证据)承接完整代码质量责任；本次实时审计、最终模板包验证与升级闸门不跳过，Release 正文列出来源。本次计划仍绑定 main 候选，模板基线独立重算。
- 作业从计划冻结的基线 tag 用 `scripts/release-commits.ps1` 独立重算"有无模板变化"，与计划不一致即失败。规划之后新增的 `template-v*`（相邻发布的上一轮、本版本重跑）不影响这一判定；发布说明与升级闸门从低于本版本的最高 `template-v*` 起算。
- 发布时版本取框架通道输出的正式版，模板默认的框架版本不等于它即失败。

## 默认框架版本

模板默认的 `LeistdFrameworkVersion` 必须是正式版。模板从框架的版本回写提交打包，默认版本即本次正式版，也就是模板自身的版本。

`template-package/test-template-package.ps1 -NuGetOrgRestore` 只在发布时运行：在仓库外生成两个代表场景，都只用 nuget.org（独立包缓存、不用 HTTP 缓存）还原该版本。

- 只有"找不到 Leistd.* 的默认版本"（NU1102/NU1103，刚推送的正式版尚未建好索引）与取不到源（NU1301）在限时内重试；包 ID 不存在（NU1101）等其他失败立即失败。
- 还原不到即不发布。

## 验证与推送

1. 打包后核对包内文件与 `git ls-files template` 一致、包内 `template.json` 只写入了本次版本与源提交。
2. 从隔离安装的包生成 `identity-all-features` 与 `resource-capabilities-03`，核对来源标记并验证重建零差异。
3. 发布且已有上一个 `template-v*` tag 时另跑升级闸门 `template-package/test-template-upgrade.ps1`：上一版生成两个代表场景并提交项目定制，同步到候选包，验收无冲突合并、冲突退出 2 与 `-Complete` 三类，verify 使用本次源码打的候选 Framework 包。
   - 只验证时（develop）不跑：自上一个 tag 起每次 push 都算模板有变化，同步逻辑由本地 `test-template-sync.ps1` 覆盖。
   - 首个模板版本没有上一版，只做上面的首版自举验证。
4. 全部通过后才创建 tag、经 Trusted Publishing 推 nuget.org、推 tag（`scripts/push-release-ref.ps1`），并创建 GitHub Release（不标记为 latest，latest 留给框架正式版）。

## 重跑

框架已发布而模板作业失败时，只重跑模板作业，按已有发布身份补齐缺的步骤：

- nuget.org 已有同版本模板包：读取其 nuspec 的源提交，相同则跳过推包，不同则停止。同一版本号不能对应两份源码。
- `template-vX.Y.Z` 已存在：指向本次源提交则沿用，否则停止。
- GitHub Release 按 tag 查找已有的，再补齐。

修复需要改代码时，由下一个同号版本补齐，该版本号只有框架包。

## 变更说明

变更说明就是该 tag 的 GitHub Release 正文，不另建 CHANGELOG：安装命令、默认框架版本、变更范围，以及按 scope 分组的模板提交（与框架发布说明同一分类）。已发布模板版本的升级说明可按需写入 `docs/template/upgrades/<版本>.md`，存在时 Release 正文链接到该 tag 下的地址。

## 本地验证与自检

- 模板包：`pwsh template-package/test-template-package.ps1`（打包、隔离安装、来源标记与重建确定性，不发布）。
- 同步脚本与升级闸门：`pwsh template-package/test-template-sync.ps1`（本地打包几个模板版本，覆盖三类验收、反例与存量接入）。
- 发布接线：`python scripts/test-workflow-change-scope.py` 覆盖模板通道。
  - main 上任一侧有变化则两侧同号发布，都没有变化则都不发布；版本回写不算模板变化；develop 只验证不推送。
  - nuget.org 还原只在发布时；升级闸门作为放行门槛只在发布且有上一个 tag 时执行，先于全部发布写操作（每个工作日的 `scheduled-checks.yml` 另以 verify Fast 运行，不阻塞发布）。
  - 版本与发布说明的实际步骤及反例：`!` 无脚注、基线或变化判定与计划不一致、develop 被要求发布、缺框架正式版本、默认框架版本不是本次正式版；只改框架时的"模板无变更"说明。
