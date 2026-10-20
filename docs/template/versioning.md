# 模板版本与发布

模板打包为 `Leistd.Templates`（`template-package/Leistd.Templates.csproj`，`PackageType=Template`），由 `release.yml` 的 `template-release` 作业发布。版本独立于框架，tag 为 `template-vX.Y.Z`，包版本由包元数据承载。

提交规范、分支与 PR、需单独授权的发布操作与下游通知同框架，见[版本与发布规范](../framework/versioning.md)。包内容、来源标记、同步与升级闸门的验证见[模板包与来源标记](./quality-assurance.md#模板包与来源标记)。

## 何时发布

自最高的 `template-v*` tag（须为候选祖先）起，有提交改动 `template/` 或 `template-package/` 时，push `main` 发布。

| 触发 | 行为 |
| --- | --- |
| push `main`，模板有变化 | 发布新的模板版本 |
| 在 `main` 上手动触发（workflow_dispatch） | 同属 stable 通道，只验证不发布模板 |
| push `develop` | 只打包并跑包内容、隔离安装、来源标记与重建确定性的验证（版本带 `-ci.<run_id>.<attempt>`）；不做 nuget.org 还原、不推送、不打 tag |
| nightly | 没有模板通道 |

- 只改框架时不升模板版本。框架 stable 的版本回写提交（`chore: 发布 vX.Y.Z [skip ci]`，改了模板默认的 `LeistdFrameworkVersion`）不计为模板变更，新的默认框架版本随下一次模板变更发布。
- develop 不做 nuget.org 还原的原因：develop 上默认版本仍是上一个正式版，之后才首次发布的框架包还没有正式版，还原必然失败。

## 版本推算

与框架通道共用 `scripts/release-commits.ps1` 的提交解析，只取改动模板输入的提交：

- `!` 或 `BREAKING CHANGE:` 脚注 → Major（0.x 期间按 Minor）；`!` 同样必须带脚注。
- `feat` → Minor，其余 → Patch。
- 没有任何 `template-v*` tag 时首版为 `0.1.0`。
- scope 只用于发布说明分组，判定看提交是否改动模板输入。

## 放行条件

质量计划的 `TemplateRelease.Required` 为真时（只可能是 push `main`），计划不得收窄，适用与框架发布相同的全部[放行责任](../framework/ci-release-gates.md#放行责任)。

- `template-release` 作业还要求同一候选 SHA 的质量汇总成功，且框架通道成功或本次无需发布。
- 作业在 `scripts/release-commits.ps1` 上独立重算基线 tag 与"有无模板变化"，与计划不一致即失败。

## 默认框架版本

模板默认的 `LeistdFrameworkVersion` 必须是正式版。框架通道本次发布了 stable 时，模板从版本回写提交打包，默认版本即本次正式版。

`template-package/test-template-package.ps1 -NuGetOrgRestore` 只在发布时运行：在仓库外生成两个代表场景，都只用 nuget.org（独立包缓存、不用 HTTP 缓存）还原该版本。

- 只有"找不到 Leistd.* 的默认版本"（NU1102/NU1103，刚推送的正式版尚未建好索引）与取不到源（NU1301）在限时内重试；包 ID 不存在（NU1101）等其他失败立即失败。
- 框架通道本次被跳过时没有刚推送的正式版，nuget.org 还原不等待索引（`-RestoreTimeoutMinutes 0`）。
- 还原不到即不发布。

## 验证与推送

1. 打包后核对包内文件与 `git ls-files template` 一致、包内 `template.json` 只写入了本次版本与源提交。
2. 从隔离安装的包生成 `identity-all-features` 与 `resource-capabilities-03`，核对来源标记并验证重建零差异。
3. 发布且已有上一个 `template-v*` tag 时另跑升级闸门 `template-package/test-template-upgrade.ps1`：上一版生成两个代表场景并提交项目定制，同步到候选包，验收无冲突合并、冲突退出 2 与 `-Complete` 三类，verify 使用本次源码打的候选 Framework 包。
   - develop 与手动触发不跑：自上一个 tag 起每次 push 都算模板有变化，同步逻辑由本地 `test-template-sync.ps1` 覆盖。
   - 首个模板版本没有上一版，只做上面的首版自举验证。
4. 全部通过后才创建 tag、经 Trusted Publishing 推 nuget.org、推 tag（`scripts/push-release-ref.ps1`），并创建 GitHub Release（不标记为 latest，latest 留给框架正式版）。

## 重跑

nuget.org 已有同版本模板包时，先读取其 nuspec 的源提交：与本次相同则跳过推包，继续补齐 tag 与 Release；不同则停止，同一版本号不能对应两份源码。

## 变更说明

变更说明就是该 tag 的 GitHub Release 正文，不另建 CHANGELOG：安装命令、默认框架版本、变更范围，以及按 scope 分组的模板提交（与框架发布说明同一分类）。已发布模板版本的升级说明可按需写入 `docs/template/upgrades/<版本>.md`，存在时 Release 正文链接到该 tag 下的地址。

## 本地验证与自检

- 模板包：`pwsh template-package/test-template-package.ps1`（打包、隔离安装、来源标记与重建确定性，不发布）。
- 同步脚本与升级闸门：`pwsh template-package/test-template-sync.ps1`（本地打包几个模板版本，覆盖三类验收、反例与存量接入）。
- 发布接线：`python scripts/test-workflow-change-scope.py` 覆盖模板通道。
  - 只改模板 / 只改打包项目 → main 上发布模板、不触发框架；只改框架或只有版本回写 → 不升模板版本。
  - develop 与 main 上手动触发只验证不推送；nuget.org 还原只在发布时，框架通道被跳过时还原不等待；升级闸门只在发布且有上一个 tag 时执行，先于全部发布写操作。
  - 版本推算与发布说明的实际步骤及反例：`!` 无脚注、基线或变化判定与计划不一致、develop 或手动触发被要求发布。
