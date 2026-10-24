# Leistd 框架版本与发布规范

## 版本来源（VERSION 文件）

框架版本的**唯一来源**是仓库根的 `VERSION` 文件（`x.y.z`），构建和发布都以它为基准。

### `VERSION` 是版本基准，平时等于上一个正式版

- 平时等于最近一次 stable 已上传的版本，由 `release.yml` 上传后回写；包源索引与 Release 验收可能稍后完成。下一版由流水线按提交推算，不提前写入。
- 维护者把它改到高于最高正式 tag 时，即显式指定下一版：beta 为 `<VERSION>-beta.<N>`，正式版原样发布，不按提交递增。用法见[发布指定版本](#发布指定版本)。
- 最高正式 tag 取全部 `vX.Y.Z` tag，不要求是候选的祖先；提交范围从它起算，递增从它与 `VERSION` 中较高者起算。正式版回写合回 `develop` 之前，beta 也不会低于已发布的正式版，也不会重复计入已发布的提交。
- 前提是该 tag 发布的源码都在候选里：除合并提交与版本回写外，它不能含候选缺少的提交，否则推包前停止。stable 遇到这种情况，说明本候选排队晚到、已被更新的发布覆盖；`develop` 遇到，说明 `main` 上有提交未合回，先合回。
- 下游跟 stable 使用 `VERSION`；跟 beta 从包源或流水线确认实际 `-beta.<N>` 全称，不猜测后缀。

### 预发布版本选择

nuget.org 上的 `1.0.0-beta.14`–`22`（2026-07）按 SemVer 排序高于 `0.13.0-beta.*`，`--prerelease` 与浮动版本会解析到这批旧包。在 `VERSION` 抬到 `1.0.0` 并合入 `develop` 之前，预发布依赖写死全称，如 `0.13.0-beta.170`。之后新 beta 的 `<N>` 远大于 22，排序高于旧包，旧包无需 unlist。

定时任务执行默认分支的 workflow；`release.yml` 合入 `main` 之前，nightly 仍按 `main` 上的旧规则推算版本。

- `framework/common.props` 在构建时自动读取 `VERSION` 作为 `VersionPrefix`，所有 `Leistd.*` 包同步该版本（无外部工具依赖）。
- 模板 `template/backend/Directory.Build.props` 的 `<LeistdFrameworkVersion>` 是字面值副本（生成项目需自包含），由发布流水线 `release.yml` 在发版时回写。

## 提交规范决定版本递增（Conventional Commits）

发版时流水线（`release.yml`）分析自上个稳定版 `vX.Y.Z` tag 以来的提交信息，算出下一个版本（默认优先最小版本）：

| 提交 | 递增 | 例 |
| --- | --- | --- |
| 普通提交 / `fix:` | Patch（默认） | `fix: 修复锁超时` → 0.8.0 → 0.8.1 |
| `feat:` / `feat(scope):` | Minor | `feat: 新增 Redis 锁` → 0.8.x → 0.9.0 |
| `feat!:` / 任意类型带 `!` / 含 `BREAKING CHANGE` | Major | `0.12.0` → **`0.13.0`**（见下方 0.x 规则）；`1.4.2` → `2.0.0` |

> **提交信息务必遵循 [Conventional Commits](https://www.conventionalcommits.org/)** —— 它直接决定版本如何递增。

### 标了 `!` 就必须有 `BREAKING CHANGE:` 脚注

带 `!` 的提交必须有非空 `BREAKING CHANGE:` 或 `BREAKING-CHANGE:` 脚注，自身说明变化与必要动作。

```
refactor!: 异常响应统一走 Problem Details 管道

BREAKING CHANGE: 失败响应的 message/details 改为标准字段 detail/errors，
调用方改用 detail/errors，各组件的 AddXxx 登记自己的异常映射。
```

脚注直接写清变化与调用方必要动作，不强制链接旧版对照文档。

`release.yml` 在推算版本时拦截带 `!` 却无非空脚注的提交并列出原因。脚注检查、升版推算与发布说明提取使用同一解析口径：`!` 看标题前缀，脚注须在行首，接受 `BREAKING CHANGE:` 与 `BREAKING-CHANGE:`。

脚注检查从固定锚点 `802b4dca` 开始；升版推算使用完整提交范围。锚点不得随时间扩大豁免范围，仅在历史重构后不再是 HEAD 祖先时更新；找不到祖先即失败。

### 0.x 期间的破坏性变更按 Minor 递增

依据 [SemVer 第 4 条](https://semver.org/lang/zh-CN/#spec-item-4)：`0.y.z` 是初始开发期，
公共 API 不承诺稳定。因此当前主版本为 `0` 时，`!` / `BREAKING CHANGE` 递增 **Minor** 而不是 Major。

`1.0.0` 代表 API 稳定承诺，由维护者按[发布指定版本](#发布指定版本)显式抬 `VERSION`，不能由一条破坏性提交自动触发。进入 1.x 后破坏性变更递增 Major。0.x 的破坏性变化仍列入 release notes，升级小版本须阅读。

## 分支与 PR

功能分支经 PR 合入 `develop`，`develop` 经 PR 合入 `main` 发版。

1. 从最新的 `develop` 拉功能分支，在分支上提交。
2. 开 PR 前先 `git fetch origin`，再 rebase 到最新的 `origin/develop`；冲突在功能分支上解决，解决后重新运行受影响的本地验证。
3. PR 指向 `develop`。`ci.yml` 在指向 `develop` 或 `main` 的 PR 上运行，按[同候选输入计划](./ci-release-gates.md#同候选输入计划)执行必过检查。
4. PR 以合并提交（merge commit）合入。版本推算与发布说明逐条读取范围内的非合并提交（`scripts/release-commits.ps1`），功能分支上每个提交的标题都要符合 Conventional Commits。
5. 合入 `develop` 触发 beta；`develop` 合入 `main` 触发正式版，属于[需单独授权的发布操作](#需单独授权的发布操作)。

PR 描述写三项：

| 项 | 写什么 |
| --- | --- |
| 变更 | 背景与改了什么，按交付面分组 |
| 验证 | 实际运行的检查与结果；未执行的检查和原因 |
| 风险 | 影响面、未验证项；破坏性变更写明下游必要动作 |

## 分支 → 包类型

| 分支 / 触发 | 版本形态 | 发布目标 | 工作流 |
| --- | --- | --- | --- |
| push `main` | `x.y.z`（正式，自动递增） | nuget.org | `release.yml`（stable 通道） |
| push `develop` | `x.y.z-beta.<N>` | nuget.org（预发布） | `release.yml`（beta 通道） |
| 每工作日定时（develop） | `x.y.z-preview.<yyyyMMdd>.<run_number>` | GitHub Packages（内部） | `release.yml`（nightly 通道） |
| push `main`，与框架正式版同号 | `Leistd.Templates` `x.y.z`（tag `template-vx.y.z`） | nuget.org | `release.yml`（template 通道，见[模板版本与发布](../template/versioning.md)） |

> 框架三个通道由**单个** `release.yml` 内部按 `github.ref` / `github.event_name` 自动判定；template 通道是同一 workflow 的 `template-release` 作业。

每个工作日另有 `scheduled-checks.yml` 对 develop 做与单次改动无关的检查（前端依赖全量审计、NuGet 漏洞扫描、模板升级闸门 verify Fast），不发布任何包，失败不阻塞 PR 与发布，见 [CI 与发布闸门](./ci-release-gates.md)。

> 预发布后缀用**点分数字**（`-beta.12`、`-preview.20260623.123`），保证 NuGet 数值排序正确。

beta 由下游试点项目验收，试点项目按上文引用确切的 `-beta.<N>` 全称。

## 正式版发布（全自动）

**push 到 `main` 即自动发布**，无需手动打 tag：

1. 对同一候选提交复用 CI 的静态、Framework、Template 矩阵和真实 PostgreSQL 验证；全部通过后按提交推算新正式版本；
2. 在本地回写 `VERSION`、同步模板，创建 `chore: 发布 vX.Y.Z [skip ci]` 提交和 tag（tag 只在此创建一次，beta/nightly 指向候选提交）；
3. 打包并隔离消费最终版本，为整组最终 nupkg 生成来源证明（见[来源证明](#来源证明)），把最终 nupkg、清单（版本、tag、发布 SHA、候选 SHA、包哈希）、发布说明与 tag bundle 上传为 Actions artifact `release-packages-<版本>`，随后经 Trusted Publishing 推 nuget.org；
4. 包源写入后立即推送 tag，stable 再把版本提交快进到 main，记录已发布产物；
5. 推送成功后直接创建 GitHub Release（自动生成 release notes，附件为全部 nupkg）。推送接口返回 201/202 即表示已接收，`dotnet nuget push` 据此以退出码判定；nuget.org 随后异步校验并建索引（官方说明通常 15 分钟内），校验失败由 nuget.org 邮件通知所有者，流水线不等待包可见。包内容与依赖在推送前已用同一批 `.nupkg` 隔离消费验证。
6. 发布后经 PR 把 `main` 合回 `develop`，带回 `VERSION` 与模板默认 `LeistdFrameworkVersion` 的回写；之后合入 `main` 时不会在这两处冲突。

机制要点：
发布差异从本通道最近已发布且为候选祖先的 tag 起算，独立于本次 push before。stable 只认正式 tag；beta 认 beta/正式 tag，排除 nightly，按 Git 祖先距离选择，不按版本字符串大小。正式 tag 在 main 的 VERSION 回写提交上，不是 develop 祖先时 beta 使用最近 beta 基线。取消或失败的发布没有完成 tag，下一次文档推送仍纳入尚未发布的框架改动；找不到可靠基线时保守发布。

框架生产源码与真实 Pack 文档（含 `framework/NuGet.md`）触发发布。明确共享发布输入包括 `VERSION`、`framework/common.props`、根 `Directory.Build.props`、`Directory.Packages.props`、`global.json` 与 `NuGet.config`；脚本、workflow、未知输入/打包规则保守发布。内部说明、仓库配置（`.github/dependabot.yml`、根 `.mcp.json`）、框架测试、模板与模板打包项目（`template/`、`template-package/`）的变化不触发框架 beta；`main` 上模板有变化时框架正式版与模板同号发布（见[模板版本与发布](../template/versioning.md#版本与发布时机)）。其余分类 stable/beta 相同。

stable 的 main push 可按[同树质量证据规则](./ci-release-gates.md#正式发布复用同树质量证据)复用第二父提交的完整 develop 验证；缺证据照常 full。本次实时依赖审计和最终发布物验证仍执行，框架与模板 Release 正文记录来源 run、attempt、SHA 与 tree。

candidate 与 CI 固定相同 SHA，显式传递质量和发布基线，CI 据此计算计划。发布必须取得成功汇总输出的 full 计划、`QualityValidated=true`、合法的 current/reuse 执行来源、全部发布责任和相同候选身份。非发布 push 也执行适用质量验证；轻量文档 push 的前次成功条件见[CI 质量计划与放行](./ci-release-gates.md#pr-的内部文档例外)。
- 回写提交带 `[skip ci]` 且过滤 `github-actions[bot]`，避免死循环。
- ⚠️ NuGet 包不可删（只能 unlist）。框架或模板每次有效变更都会产出一个正式版，请把控合入 main 的节奏。

### 发布指定版本

用于进入 `1.0.0` 等需要人为决定的版本号，不需要手工打 tag：

1. 功能分支只把 `VERSION` 改为目标版本（须高于最高正式 tag），提交 `chore: 版本基准改为 1.0.0`，经 PR 合入 `develop`。`VERSION` 是共享发布输入，合入即发布 `1.0.0-beta.<N>`，供试点验收。
2. `develop` 合入 `main`（需单独授权），框架与模板同号发布 `1.0.0`，模板默认框架版本即 `1.0.0`。
3. 按上文第 6 步把 `main` 合回 `develop`，之后恢复按提交递增。

`python scripts/test-workflow-change-scope.py` 执行实际的版本推算步骤，覆盖指定版本的 beta 与 stable、未合回时 beta 高于已发布正式版，以及 1.x 的破坏性变更递增 Major。

### 部分发布恢复

包源、tag 与 Release 是相互独立的系统，推包也逐包进行，流水线无法让它们原子完成，只保证失败后可以按原身份补齐。

- **tag 推送**：`scripts/push-release-ref.ps1` 只推送、不重建 tag，有限次短重试；每次推送前查询远端，同名 tag 已指向预期提交即视为完成，指向其他提交即停止，不 force、不删除。stable 的 main 回写单独推送，只允许从候选 SHA 快进到发布 SHA。git 退出码不区分权限错误与瞬时故障，重试不解析错误文字。Release 沿用 `softprops/action-gh-release` 自带的创建重试与按 tag 查找，不再叠加重试。
- **恢复指引**：失败时 job summary 由 `scripts/write-release-recovery-summary.ps1` 按步骤 outcome 与推送结果判定阶段，写出版本、tag、实际发布 SHA 与通道对应的命令；推包未开始的失败不给补 tag 指引，nightly 没有 Release。不要整体重跑发布 job：它会重新推算并重打同一版本。
- **从原产物补齐**：从失败 run 的 artifact `release-packages-<版本>` 取回原 nupkg、`manifest.json`、`release-notes.md` 与 `release.bundle`。按清单逐个核对包源，只推缺失的原包；tag 从 bundle 取回原对象，核对它解引用后等于清单的 `releaseSha` 再用上述脚本推送（stable 的版本提交只存在于 bundle 中，不能拿候选 SHA 代替）；Release 按 tag 查询实际状态，只补缺失的 Release 或附件（nupkg 取自原产物），不覆盖已有附件。
- **不自动 unlist**：包完整上传且内容无误时，缺的只是 GitHub 元数据，补齐即可。unlist 只降低可发现性，不撤销发布，按精确版本仍可还原，版本号也不能再用。发现错误或不安全的包时，由维护者明确决定 unlist 或 deprecate。

tag 记录已发布版本，不能因发布后发现的问题而删除后复用版本号。候选或产物改变时使用新版本并说明旧版本状态。恢复后手工确认目标包源能还原全部精确版本。不要用 `--skip-duplicate` 掩盖不同产物。

`python scripts/test-workflow-change-scope.py --release-publish` 以本地 bare 仓库作远端，验证首次推送失败后重试成功、服务端已写入但本地报错、远端同名 tag 已指向同一提交、冲突 tag 不改写、远端查询失败按未知状态重试而不判冲突、重试用尽后 summary 的版本/SHA/阶段；并实际执行 tag 与产物步骤，在只有候选提交的检出里逐条执行 tag 阶段与 main 阶段 summary 给出的命令完成恢复，去掉其中的取回步骤时必须失败。

## 发布说明

`release.yml` 按 Conventional Commits 生成 release notes，`!` 与 `BREAKING CHANGE` 脚注进入“破坏性变更”。每个小节内按提交的 scope（组件家族，如 `settings`、`multi-tenancy`）分组，没有 scope 的条目归入最后的“未标注范围”。Release 正文就是变更记录，不另建 CHANGELOG 文件；因此提交标题的 scope 应写组件家族名。公共 API、配置、路由、授权、数据库模型与译文变化同步源码、消费者和当前文档；不保留过渡 API 或旧版对照。

stable 或模板版本发布后，按下游清单通知使用方（目前只有 CRM）：给出版本与 Release 链接，含破坏性变更时附上必要动作。

开发期不强制独立升级指南。用户明确要求迁移说明时按需提供，发布工具仍支持 `docs/framework/upgrades/<基础版本>.md` 这一可选文件；没有指南不能推断无需改代码，最终用法以目标包文档、XML 和程序集为准。

### 包与 Release 链接

`PackageReleaseNotes` 固定到本次 tag，不使用可变分支链接：

| 条件 | 包发布说明 |
| --- | --- |
| 有可选指南 | 指向指南在本次 tag 下的地址，Release 正文追加同一链接 |
| 无指南，stable / beta | 指向本次 GitHub Release |
| 无指南，nightly | 指向该 tag 的提交页，nightly 不创建 Release |

`python scripts/test-workflow-change-scope.py` 执行实际 workflow 步骤，验证三个渠道的有／无指南链接及夹具包 `.nuspec`、以及发布说明的 scope 分组。本地打包不设置 `PackageReleaseNotes`。

当前不启用 Package Validation：开发期以最终 API 和真实消费验证为准；建立 API 稳定承诺时再评估兼容性基线。

## 需单独授权的发布操作

- 合入 `main`：框架或模板任一侧有变化，就同号发布框架正式版与 `Leistd.Templates`；
- 首次发布 `Leistd.Templates`：没有任何 `template-v*` tag 时，下一次合入 `main` 即首发，版本与该次框架正式版相同——申请授权时须写明"同时首发 Leistd.Templates <版本>"；
- nuget.org 上的 unlist 或 deprecate；
- 修改仓库设置，如分支保护与必需检查。

## 来源证明

发布作业在整理最终产物之后、推包之前，用 [`actions/attest-build-provenance`](https://github.com/actions/attest-build-provenance) 为同一批最终 nupkg 字节逐个生成 SLSA 来源证明（需 `attestations: write` 与 `id-token: write`）。消费者可验证：

```bash
gh attestation verify Leistd.Core.<版本>.nupkg -R zengqinglei/leistd-net
```

来源证明在推包前完成，失败时属于“推包未开始”，包源与远端都没有本版本；已生成的来源证明只对应未发布的字节，无需处理。Release 附件缺失时按[部分发布恢复](#部分发布恢复)从原产物补齐。

## 依赖更新

`.github/dependabot.yml` 只覆盖第三方 NuGet（`framework/` 与 `template/backend/` 的 `Directory.Packages.props`）与 GitHub Actions，每周分组提版本更新 PR：minor/patch 一组，major 单独一组。版本更新经 `target-branch` 进 `develop`；Dependabot 只读默认分支（main）上的配置，改动随下次 develop 合入 main 生效。安全更新不受 `target-branch` 控制，仍指向 main，按分支模型移到 develop 处理。框架与模板共有的第三方依赖（如 Refit）在同一个 PR 里一起升：模板生成项目同时引用框架包，框架包把依赖下限提高、模板仍直接钉着更低版本时会因 NU1605（降级）还原失败；Dependabot 按目录分别提 PR 时，在其中一份补齐另一个目录，另一份关闭。`Leistd.*` 由本仓库统一版本发布，不在更新范围内。依赖 PR 与其他 PR 走同一套质量验证。

模板前端 npm 依赖不交给 Dependabot：两份 `package.json` 与锁文件含条件行，不是合法 JSON。默认与本地化两份依赖文件和锁文件按[前端依赖维护](../template/quality-assurance.md#前端依赖维护)的规则成对更新。

## 鉴权

- **nuget.org**（stable / beta）：Trusted Publishing（OIDC，免长期 API Key）。需在 nuget.org 配置信任策略（仓库 + 工作流文件名 `release.yml`），并设 `NUGET_USER` secret。
- **GitHub Packages**（nightly）：用内置 `GITHUB_TOKEN`，无需额外配置。

### 引用 nightly 包（内部测试）

GitHub Packages 需认证拉取，即使公开仓库：

```bash
dotnet nuget add source "https://nuget.pkg.github.com/zengqinglei/index.json" --name leistd-nightly --username <你的GitHub用户名> --password <PAT，需 read:packages> --store-password-in-clear-text

dotnet add package Leistd.Core --prerelease
```

## 本地手动操作（不发布）

```bash
# 打包候选包到本地 feed（产出前先清空），版本为 <VERSION 下一补丁>-local.<UTC 时间戳>
pwsh framework/build/pack-local-feed.ps1

# 指定下一版：经 PR 修改 VERSION，见「发布指定版本」
```

> `.tmp/local-feed` 仅用于本地开发并由 `.gitignore` 忽略；CI 发布继续使用 `framework/artifacts`。版本推算、模板同步、打包发布逻辑在 `.github/workflows/release.yml`，提交解析与升版判据在两条通道共用的 `scripts/release-commits.ps1`；发布由 push 自动触发，本地通常无需手动介入。模板包的本地验证见[模板版本与发布](../template/versioning.md#本地验证与自检)。

### 候选包版本

验证用的包（本地 feed 与 CI 的 `framework-pack`）不使用 `VERSION` 本身，而是 `<VERSION 下一补丁>-<后缀>`：CI 为 `-ci.<run_id>.<attempt>`，本地默认 `-local.<UTC 时间戳>`（`-VersionSuffix` 可改）。带预发布后缀的版本不会与任何已发布版本相同，消费方不会因版本号相同而用到 nuget.org 上的包。

- 包源目录里的 `candidate.json` 记录候选版本与打包所用 SDK（`dotnet --version`）。仓库根 `global.json` 只锁定 .NET 10.0 的主次版本（`10.0.300` + `rollForward: latestFeature`），feature band 与补丁允许浮动；每次运行实际使用的 SDK 以 `candidate.json` 记录为准，SDK 不同的运行不放在一起比较耗时。
- 矩阵、PostgreSQL/OIDC 端到端与 `test-package-consumption.ps1` 都从 `candidate.json` 读取版本：一次性 NuGet 配置用 `packageSourceMapping` 让 `Leistd.*` 只走本地源；生成项目经外层 `Directory.Build.rsp`（`-p:LeistdFrameworkVersion=<候选>`）接收版本，不改模板源，生成项目自己的 verify 脚本起的子进程同样生效。
- 候选版本号每次运行唯一，nuget.org 与之前的运行里不会有同号包，mapping 之外不需要再核对包字节；restore 后只核对解包目录里的每个 `Leistd.*` 都是候选版本，并顺带读 `.nupkg.metadata` 确认来自候选包源（`framework/build/candidate-feed.ps1`），正反例见 `framework/build/test-candidate-feed.ps1`。
- 发布作业打出的最终版本包同样写入 `candidate.json`，经同一断言消费验证。

## 注意

- CPM 下第三方包版本集中在 `framework/Directory.Packages.props`；升级第三方依赖按提交规范评估影响。
- monorepo 统一版本：所有可发布框架包共享同一版本。推包途中失败可能留下部分已发布的包；同版本不可覆盖，恢复前须核对实际包集合和 tag，不用 `--skip-duplicate` 掩盖不同候选产物。
- 版本机制仅使用 git、PowerShell 与 MSBuild，不依赖外部版本工具。
