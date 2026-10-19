# CI/CD 与本地验证：面向 AI 开发节奏的重构

> 在途计划（2026-10-18）。已确认的决策有两项：
> - 覆盖义务绑定放行闸门，不再由每个 PR 承担；
> - 模板改为独立版本、模板包、生成标记加三方合并同步。
>
> 完成后按仓库惯例，把有效结论上收到稳定文档并删除本文。本计划跨 Framework、Template、Skill、CI 与发布，按 `maintaining-leistd-repository` 统筹。每个阶段开始时，先在本文对应小节下补任务级拆分，再按“阶段改完先评审再提交”推进。

## 1. 现状（2026-10-18）

### 1.1 CI 实测

样本取自 `gh run view --json jobs`：10-16 至 10-18 最新 12 次成功 PR，以及 12 次成功的 develop release push。不含失败、取消和重跑。墙钟按“创建到最后一个质量作业结束”计算。

| 指标 | PR·完整阶段（n=5） | PR·前端阶段（n=7） | develop push，full（n=12） |
| --- | --- | --- | --- |
| 墙钟中位 | 1987 s | 859 s | 1807.5 s |
| 质量作业时长合计中位 | 5511 s | 2919 s | 5826 s（含 candidate/release 为 5969 s） |
| 最长 / 最短分片 | 1896 / 561 s | 759 / 287 s | 1694 / 1115 s |
| 全部有效形态生成作业中位 | 686 s | 612 s | 584.5 s（样本区间 395–695 s） |

结构性问题：

1. **每个 PR 承担全部覆盖。** 只改框架的 PR 也选中全部 18 个 PR 场景：`scripts/plan-quality-checks.py` 的 `create_plan` 框架分支只收窄测试和消费项目，不收窄 `Scenarios`。
2. **合入后全量重跑。** develop push 会对同一份代码再跑一次 full，不复用 PR 结果。完整阶段 PR 与 develop 两个中位相加约 11,300 s，这只是估计值，不是逐次合入的实测中位。
3. **510 s 不可达。** 生成作业本身就超过 510 s，只要它和完整矩阵仍在每个 PR 上，目标就达不到。
4. **候选身份缺口。**
   - 候选包版本等于 `VERSION`（0.12.0），与 nuget.org 上的已发布版本同号。
   - 矩阵主路径的 NuGet.Config 同时列出本地源与 nuget.org，却没有 `packageSourceMapping`（`scripts/test-template-matrix.ps1:771-783`），也没有核对包的来源。
   - 隔离消费脚本和矩阵的容器分支已经配置了 mapping。
5. **放行责任不完整。** 发布候选的 `ContainerSmoke` 仍由输入分类决定（`create_plan` 的 `finish`）。例如 develop run 39121002601 的 full 就没有容器责任，因此“full 成功”不等于“容器已验证”。
6. **缓存。** 已有 setup-node 的 npm 缓存；第三方 NuGet 包、Playwright 浏览器和镜像都没有缓存。

### 1.2 本地验证（诊断数据）

样本：30 天内 23 个主会话加 272 个子 agent 的转录。统计脚本在会话 scratchpad 中，不入库，以下数字仅作诊断，不作为验收基线。

- 本地验证约占活跃时间的 30%（88 / 295 h），基本是串行执行。
- 时间分布：
  - 模板矩阵约 43 h，其中 `-Tier pr` 与不带场景的全量运行约 17.5 h；
  - `check-all` 共 535 次、约 12 h；
  - PG/OIDC 端到端约 8 h。
- 没有编辑就重跑的约 665 次。这是上限估计，因为检测不到其他终端里的修改。
- agent 轮询等待远端 CI 或评审约 105 h。这是 agent 被占用的时间，不是 CI 执行时间。

根因：

- 规范要求本地 L1 对所选产品跑完整阶段，共享输入时还要跑完整 PR 档；
- 生成项目每次都从零生成并重新安装依赖；
- `check-all` 只能整体运行；
- CI 反馈慢。

### 1.3 交付面缺口

**Framework**

- 没有 Package Validation，SemVer 只依据提交解析（0.x 期间维持现状，见 P3）；
- 发布说明只有 GitHub Release 正文；
- 没有 SBOM 和来源证明，也没有依赖更新机器人；
- 没有 `global.json`；
- nuget.org 上仍为 listed 的旧预发布版本排序高于当前 0.x，干扰 `--prerelease` 解析。已确认的有 `Leistd.Core` 的 `1.0.0-beta.14/.15/.16/.17/.18/.21/.22`；其他包和 preview 版本以授权前的清单为准。

**Template**

- 没有版本，也不打包发布；
- 生成项目里没有来源标记，也没有任何升级手段；
- 规范在生成后归项目所有；
- `docs/README.md` 的分发边界禁止在载荷里写迁移步骤。

**Skill 与规范**

- `measure-template-read-cost.py --check` 的五类任务全部超出上限，而且没有接入闸门；
- 质量文档把“该跑什么”和“CI 内部机制”混在一起写；
- 生成项目没有可执行的浏览器 E2E，`testing.md` 却要求核心闭环做 E2E；
- Spartan Skill 有两份副本。

## 2. 决策

1. **PR 有意不承担全局覆盖，放行候选必须完整。**
   - 每个放行产出之前，第 4 节 P2a 的放行责任表全部成功。放行产出包括 develop beta、main 正式版和模板发布。
   - PR 只提供第 4 节 P2b 写明的增量保证。这是责任迁移，不是等价接替：全局条件行覆盖和两两组合只由放行前的 full 证明。
   - 只在非代表场景出现的缺陷，可能在 develop full 才被发现。这是接受的代价，因为它们不会进入任何发布物。
   - 场景一个不删。
2. **成本口径。**
   - 区分两个量：“总交付成本”是每次合入的全部 PR 尝试加合入后运行；“纯质量成本”只算质量作业。
   - 按完整、前端、后端、框架、文档路径分别统计，失败、取消和重跑都计入。
   - PR 墙钟预算保留 L2 的 510 s。
   - 达标判断以第 4 节 P2b 的观测规则为准，不恢复事先登记的配对试验。
3. **删除或替换正确性检查**，仍按质量规范完成接替与变异验收。
4. **以下约束保留**：不留实验开关；不并存两套调度，也不并存两版 schema；不放宽 PG、OIDC、Chromium 和容器检查；Mac 本地结果不代替 Linux CI。
5. **本地只产出定向证据。** 本地全量矩阵只用于复现 CI 失败（`-Scenarios <失败场景>`）。仓库维护配方与生成项目测试规范分别维护，项目 Skill 只链接随项目分发的文件。
6. **模板成为有版本的产品。**
   - 采用独立版本 `template-vX.Y.Z`，打包为 `Leistd.Templates`。
   - 生成项目写入来源标记。
   - 升级时以相同输入分别重建旧版和新版，再三方合并进项目；语义冲突由项目 agent 解决。
   - 模板的变更说明使用该 tag 的 GitHub Release 正文，不另建 CHANGELOG 文件。
7. **框架以外部消费者为第一视角。**
   - 0.x 期间不做 API 兼容拦截，破坏性变化由提交标记进入发布说明，1.0 时再评估 Package Validation；
   - 发布物带 SBOM 和来源证明；
   - 73 个包继续统一版本；
   - 发布说明在现有 release notes 基础上扩展，不建第二份事实源。

## 3. 全局约束

- 每个阶段结束时，CI 只有一套调度规则、一版计划 schema；旧档位和旧字段一并删除。
- 覆盖不变量由汇总校验或发布前校验以非零退出来保证，不能只靠文档承诺。
- 容器沿用全局约定：
  - 先探测 `windows` 上下文，失败再回退 `orbstack`；
  - 只清理自己创建的资源；
  - 不使用 `docker system prune -a`。
- 凭据只走 Trusted Publishing、`GITHUB_TOKEN` 或用户提供的通道，不读取任何明文密钥。
- **以下操作需要单独授权**：
  - 合入 main（会触发正式发布，包括把 nightly 规则带入 main）；
  - nuget.org 上的 unlist 或 deprecate；
  - 修改仓库设置，如分支保护和必需检查；
  - 首次发布 `Leistd.Templates`。
- 分发载荷（`framework/docs/`、`template/`）只写已经成立的公共契约。已发布模板版本的升级说明属于公共契约；仓库内部的计划、流程和未实施的方案仍然禁止写入。

## 4. 阶段

执行顺序：P0 → P2a → P1 → P2b → P3 → P4a → P4b → P5。

- P1 中“条件改动的本地配方”依赖 P2b 的选择器，因此安排在 P2b 之后。
- P1 的其他部分可以与 P2a 并行。

### P0 候选包身份与 SDK

**目标**：每条消费路径都能证明，用到的 `Leistd.*` 包就是本次候选源码打出来的包。

- **候选版本号**
  - `framework/build/pack-local-feed.ps1` 增加 `-VersionSuffix` 参数。
  - CI 打出的包版本为 `<VERSION 下一补丁>-ci.<run_id>.<attempt>`；本地默认 `-local.<时间戳>`。
  - 产出写入 `candidate.json`，记录版本与包哈希。
- **下游一律显式接收候选版本**
  - 涉及：矩阵、PG/OIDC 端到端、`test-package-consumption.ps1`，以及生成项目自己的 `verify`。
  - 生成项目通过 `-p:LeistdFrameworkVersion=<候选>` 或 `Directory.Build.props` 外层覆盖接收版本，不修改模板源。
  - 验收要实际运行生成项目的 `verify`，证明子进程同样消费候选版本。
- **mapping 加来源断言**
  - 所有一次性 NuGet.Config 都配置 `packageSourceMapping`：`Leistd.*` 只走本地源。
  - restore 后核对每个 `Leistd.*` 的 `.nupkg.metadata` 中 `source` 是否为本地源，并核对包哈希与 `candidate.json` 一致。
- **SDK 固定**
  - 根目录新增 `global.json`：`version: 10.0.300`，`rollForward: latestFeature`。这锁定 .NET 10.0 主次版本，CI 与本地都允许更高的 feature band 和补丁。原因是开发机为 10.0.3xx、CI 为 10.0.4xx，只锁 feature band 会让本地无法构建。不追求精确复现。
  - CI 的 `setup-dotnet` 读取 `global-json-file`。实际 `dotnet --version` 写入候选证明，并作为性能比较的条件之一（SDK 不同的运行不放在一起比较）。
  - 生成项目不继承根 `global.json`；模板的 SDK 要求另在 P4a 决定。

**验收（反例）**

1. 解包目录为空、本地源缺包时，restore 失败，不会回落到 nuget.org。
2. 预先在解包目录放入一个同版本、来自 nuget.org 的包时，来源与哈希断言失败。mapping 在命中 global-packages 时会被绕过，所以这里依靠断言拒绝。
3. 矩阵回执记录候选版本，`check-template-matrix-results.ps1` 核对它与 `framework-pack` 的产出一致。

### P2a 放行责任完整化（先于任何 PR 收窄）

**放行责任表**（beta、stable、template 三条通道都适用）：

| 责任 | 判据 |
| --- | --- |
| full 全部登记场景 | 所有场景的 Backend（含后端单测和集成测试）与 Runtime 为 pass；有前端的场景另要求 Lint/Frontend/Test（Test 为前端测试）为 pass，且真实 Chromium 执行的用例数 > 0；无前端场景这三项保持 not-applicable |
| 两个容器场景 | `standalone-external-login` 与 `resource-capabilities-03` 都执行容器阶段且为 pass |
| PG 端到端、OIDC HTTP 端到端 | 作业为 success，且执行的用例数 > 0 |
| OIDC 浏览器闭环（P5 落地后加入） | 浏览器阶段为 success，且执行的用例数 > 0；只有 HTTP 阶段成功不能满足这一项 |
| 生成证明 | 320 个形态、768 组原始输入映射、角色等价、Ci 变体、`verify` 步骤与失败传播，全部有证明 |
| 框架 | 全部测试项目都有回执；包内容检查与隔离消费检查通过 |

改动：

- planner 对发布候选无条件把 `ContainerSmoke` 设为 true，PG/OIDC 不允许出现 not-applicable。
- `template-matrix` 汇总对发布候选逐项核对上表。
- 保留现有断言“`$ContainerScenarios` 两个成员属于每个档位”。

**验收（反例）**：以下每种情况都要让发布汇总以非零退出：

- 计划把 PG 或 OIDC 标成不适用；
- 缺少纯 API 容器的阶段结果；
- 浏览器用例数为 0；
- 某个责任作业失败、取消或缺少证明；
- 缺少任一项生成证明。

对照组：一份合法的纯 API 回执（三项为 not-applicable）应当通过；把纯 API 场景伪造成 `Test=pass` 应当被拒绝。

### P1 本地验证循环

**目标**：常见局部改动的本地验证能在分钟级完成，不再承担 CI 的完整回归。

1. **`check-all.ps1` 增加 `-Only <闸门名…>`**，供修复单道闸门时复跑。
   - 并行执行按实测收益决定保留与否。
   - 若保留，`-StopOnFirstFailure` 的语义改为：停止调度新闸门，等已启动的闸门结束，汇总后退出。
2. **模板开发工作区** `scripts/template-dev-workspace.ps1 -Scenario <名>`，先只做单场景的最小实现。
   - 首次运行：生成到 `.tmp/dev/<场景>/`，执行 restore 和 `npm ci`。
   - `-Sync`：从模板源重新生成到临时目录，再镜像进工作区。镜像会删除模板中已删的文件，保留 `bin/`、`obj/`、`node_modules/`、`.cache/`。
   - 依赖刷新：
     - package 或 lock 文件的摘要变化时执行 `npm ci`；
     - 候选包版本或 `*.props` 变化时重新 restore。
   - 防止丢失修改：记录上次同步写入的文件摘要。工作区里有与记录不符的修改时，`-Sync` 停止并列出这些文件，不会静默覆盖。
   - 锁的范围：锁覆盖工作区的整个使用期（同步、构建、测试），用 `-Release` 显式释放。
   - 方向只有“模板源 → 工作区”一种，不提供反向复制。
3. **仓库维护配方。** 改写 `docs/framework/quality-assurance.md` 的 L1，与 `docs/template/quality-assurance.md` 同步；仓库维护 Skill 只链接这里。

   | 场景 | 本地做 | 本地不做（交给 CI） |
   | --- | --- | --- |
   | 框架组件实现 | 构建，加选择器给出的受影响测试项目；公共 API 或包依赖变化时打包，并做 `-PackageIds` 隔离消费 | 模板矩阵、PG/OIDC 端到端 |
   | 模板后端局部源码 | 工作区 `-Sync`，加目标测试类；数据库或租户相关时加目标集成测试类 | 多场景完整阶段 |
   | 模板前端局部源码 | 工作区 `-Sync`，加目标 spec 和 lint 缓存；交互变化时在 Mock 站点做浏览器验证 | 全量 spec、多场景 |
   | 条件块、`template.json`（P2b 之后） | 用 P2b 选择器算出场景，`-GenerateOnly` 加格式与链接检查；挑一个场景在工作区里构建 | 完整的 full 档 |
   | CI 与闸门脚本 | 被改脚本的自检与夹具 | 远端调度验收之外的全量 |
   | 内部文档、Skill | `check-all -Only` 相关闸门 | 其余 |

4. **证据复用。**
   - 写入规则：证据仍然有效、也没有新的验证需要时，不重复执行相同的验证。源码、依赖、fixture、平台或安全数据源（如审计库）变化后需要重验。推送之后，不在本地重复 CI 已承担的完整责任。
   - 等待 CI 时，在后台运行 `gh run watch <id> --exit-status`，靠完成通知唤醒，不写 sleep 轮询循环。
5. **生成项目侧**（属于分发载荷，需要用生成结果验证）。
   - `verify.ps1` 新增 `-Fast`：只跑静态脚本、构建、UnitTests 和 lint。默认行为不变，`-List` 同时列出两种模式的步骤，矩阵仍用默认模式。
   - `testing.md` §1.1 改为：项目把生成的 CI 设为必过检查后，本地以 `-Fast` 加目标集成测试为准。
   - 项目 Skill 的验证段落只链接 `docs/standards/testing.md` 与 `scripts/verify.ps1`。

**验收**

- 工作区：分别新增依赖、移除依赖、更换候选包版本、删除模板文件、修改未同步文件，再让两个使用者争用同一工作区，结果都符合上面的规则。
- 隔离生成目录：检查全部 Skill 链接，并实际执行 `verify -Fast` 与默认 `verify`。
- 计时：框架和模板局部改动各取不少于 5 个同类任务，在工作区已预热的条件下计时，中位不超过 5 分钟。

### P2b PR 收窄

**档位**

- 删除 `pr` 档，改为 `core` 与 `full` 两档。
- `core` 固定包含：
  - `identity`：默认产物；
  - `identity-all-features`：Verify 场景；
  - `standalone-external-login`：Standalone 含前端，属于容器场景；
  - `resource-capabilities-03`：Resource 纯 API，属于容器场景。
- 每个场景一片。最终成员按实测耗时确定，但两个容器场景必须保留在 `core`。

**PR 的增量保证**：选择规则在 planner 中实现，汇总独立重算预期集合。

- **场景**：`core` 与受影响产品取交集。受影响产品是指 base 树和 head 树中，改动文件的生产者的并集，删除和重命名计入两端。
- **改动的文件含条件块时**，追加最小场景集合，使被改动的每个条件块的每个叶子分支（含嵌套分支和 `elif` 的每一臂）在开启侧至少生成一次；文件被裁掉的那一侧也至少生成一次。
- **以下情况退回 full**：
  - `template.json` 的 sources、modifiers、symbols 或 computed 有变化；
  - 改到共享构建配置；
  - 出现未建模或未知的输入；
  - 基线无效。
- **无条件源码**（不含条件块的文件）只保证 `core` 内的消费者。共享声明可能破坏只存在于非 `core` 场景的消费者，这类缺陷由 develop full 发现，属于决策 1 接受的代价。
- **只改框架时**，取 `core` 作为下游冒烟，不跑形态生成。
- **PG/OIDC**：命中 planner 登记的路径（数据、迁移、多租户、认证、令牌、Security 相关模板路径及框架家族）时才在 PR 上运行，否则 PR 记为 not-applicable，由放行前的 full 承担。

**生成作业拆片**：先列出原作业的证明全集，再拆分。

- 320 个形态按序号取模分为 3 片。
- 768 组原始输入映射、角色等价比较、Ci 变体、`verify` 检查划入唯一的“对照片”，它自己重新生成所需的代表产物，不引用其他片的产物。
- 汇总核对：各片候选与源码摘要一致；形态没有缺号、没有重复；对照片的各项证明齐全。

**其他**

- 缓存第三方 NuGet 包，按 `Directory.Packages.props` 做键，排除 `Leistd.*`。Playwright 和镜像缓存先测量准备阶段耗时，有净收益才加。
- full 档静态重平衡：按最近 full 运行中各场景的耗时区间分配成员，验收时计入新增分片的准备成本。
- 替换质量规范中“最多三组”和“缓存需同条件证据”两条约束。
- develop full 失败时，沿用 Actions 的失败通知，不另建 issue 机制。

**验收**

- **选择器反例**，每种都要选中正确的场景或退回 full：
  - 嵌套叶子分支；
  - `elif` 分支；
  - computed 变化影响未编辑的文件；
  - 文件被裁掉的一侧；
  - 删除或重命名；
  - modifiers 变化。
- **共享声明反例**：用夹具写明它只在 full 被发现。
- **生成拆片反例**，每种都让汇总失败：
  - 去掉某一片；
  - 删除 Ci 比较；
  - 取消等价检查；
  - `verify` 失败后仍继续执行。
- **成本观测**
  - 先用旧口径，以列明 run ID 和 attempt 的方式重建“每次合入”基线（含 PR 到合入的映射）。
  - 切换后观测 7 天，每类路径至少 5 次合入才作判断，样本不足时不宣称达标。
  - 达标要求：PR 墙钟中位 ≤ 510 s；各路径的总交付成本中位低于基线。

### P3 框架发布物

- **API 兼容检查：0.x 期间不启用**（2026-10-19 用户决定）。Package Validation 让每次打包和 restore 都多出基线比对与下载，每个改动公共 API 的 PR 还要维护登记文件，0.x 频繁破坏时收益低于成本。保持原规范“建立 API 稳定承诺（1.0）时再评估”；破坏性变化仍由提交标记（`!` 或 `BREAKING CHANGE:` 脚注）进入发布说明。
- **发布说明**：在现有 release notes 中按家族 scope 分组，Release 正文就是变更记录。
- **SBOM 与来源证明**
  - 用官方 `microsoft/sbom-tool` 为最终发布的整组 nupkg 生成 SPDX 格式的 SBOM，作为 Release 附件。
  - 用 `actions/attest-build-provenance` 为每个最终 nupkg 生成来源证明。
  - 两者都绑定最终包的字节；失败时沿用现有的部分发布恢复机制。
- **Dependabot**：对第三方 NuGet（framework、template backend）与 GitHub Actions 分组提 PR。模板前端 package/lock 含条件指令行，不由 Dependabot 维护，沿用生成副本更新后打回两份锁文件的规则。
- **unlist 旧预发布版本（维护者操作，需授权）**
  - 授权前列出精确清单：每个包仍为 listed、且排序高于当前版本线的全部版本。
  - 恢复验收：用官方客户端对代表包做预发布选择，结果回到当前线；旧的精确版本仍可还原。
- **后置候选，本计划不实施**：main 复用 tree 相同的 develop 运行结果。启用前需要先拿出 main/develop 同 tree 的命中率与可省成本，并给出复用条件（run/attempt、受信 workflow、责任集合、工具链）以及不满足时重跑的路径。

**验收（夹具）**

- SBOM 中的包哈希与最终 nupkg 一致；`gh attestation verify` 通过。

### P4a 模板包、版本与来源标记

- **模板包**
  - 新增官方的模板打包项目（`PackageType=Template`），包版本由包元数据承载。
  - `release.yml` 新增 template 通道，独立于框架的 `ReleaseRequired`：自上一个 `template-v*` tag 起 `template/` 有变化时，按 `(template)` 等 scope 推算版本并发布。
  - 只改模板时发布模板；只改框架时不升模板版本。
  - 模板发布要求同一 SHA 的放行责任表全部通过，并且生成项目只用 nuget.org 就能还原默认的 `LeistdFrameworkVersion`。也就是说，模板发布必须晚于它所依赖的框架正式版。
- **来源标记** `.leistd/template.json`，由 `dotnet new` 替换写入，标记本身也纳入形态断言。内容包括：
  - 模板包版本与源 SHA；
  - 生成名称（`-n`）；
  - 全部 parameter 取值；
  - 各项目实际生成的 `UserSecretsId`；
  - 生成时的 SDK 版本。
- **重建确定性**：重建时把 `UserSecretsId` 归一化为标记中记录的值。验收要求同一模板版本、相同输入重建后，与原产物零差异。
- **首版自举**：首版只验收本地 nupkg 的安装、生成、标记写入与重建确定性；模板候选通过本地 nupkg 安装，不提前公开发布。
- **规范归属拆分**
  - `template/docs/standards/` 只放模板基线，同步时整体更新。
  - 项目自己的规则写入 `docs/project/`。规则冲突时以项目规则为准，在 `docs/README.md` 中写明。
  - 同步修改：
    - `docs/README.md` 的分发边界，区分已发布版本的升级契约与仓库未实施的计划；
    - `docs/architecture/collaboration-scenarios.md` §2 与 §4.3；
    - 项目 Skill 的 documentation reference；
    - `template/docs/README.md`；
    - `check-retired-terms.ps1` 的豁免范围。

### P4b 三方同步与升级闸门

**同步脚本**（分发）：`template/scripts/sync-template.ps1 -To <版本>`。

- **前提**：开始一次同步（`-To` 或 `-Adopt`）时，git 工作区必须干净，`-To` 还要求标记文件存在。缺少标记时以明确错误退出。`-Complete` 允许存在本轮解决冲突产生的未提交修改。
- **基线重建**：在隔离 hive 中分别安装旧版和新版模板包，按标记里的输入生成基线和目标，并对两者做相同的归一化。如果新版删除或改名了某个参数，就以诊断信息失败，并指向该版本 Release 中的迁移说明。
- **合并规则**：
  - 文本文件用 `git merge-file`。
  - 二进制文件：项目未改，采用上游；只有项目改了，保留项目版本；双方都改了，保留项目版本，并写出 `<文件>.template-new`，记为冲突。
  - 锁文件：依赖清单合并成功后，用锁定工具重新生成（`npm install --package-lock-only`，NuGet 走正常 restore）；依赖清单本身冲突时，锁文件也记为冲突。
  - 删除与修改相冲突的情况单独列出。
- **退出码与标记**：
  - 没有冲突时退出 0，并更新标记。
  - 有冲突时退出 2，保留冲突现场，不更新标记，同时写入待完成记录 `.leistd/sync-pending.json`。记录内容：基线来源、目标包版本、各冲突路径及类型（文本、二进制、删除/修改）。
- **`-Complete`**：
  - 没有待完成记录时拒绝执行。
  - 逐项核对：文本冲突的标记已清除；二进制冲突的 `.template-new` 已删除，表示已做取舍；删除/修改冲突的路径已明确保留或删除，并已从待完成记录中逐项确认。
  - 全部通过后写入记录里的目标标记，并删除待完成记录。
- **存量项目接入（一次性引导）**：适用于没有标记的已有项目，如 CRM-V2。
  - 命令：`-Adopt -BaseSource <某 SHA 的模板源目录> -Name <名> -Params <…> -To <已发布的包版本>`。
  - 流程：
    1. 项目 agent 推断输入；
    2. 证明从源码重建的基线与项目首个提交的差异只来自项目自身的改动；
    3. 直接以“源码基线 → 指定的已发布包”做三方同步，冲突处理与 `-Complete` 规则同上；
    4. 只写入目标包版本的标记，此后完全走正常的包版本流程，不保留源码基线模式。
  - 项目已改过的 standards 内容在接入时迁入 `docs/project/`。

**项目 Skill**：`leistd-project-workflow` 新增“同步模板”意图，以及 `references/template-sync.md`。步骤如下：

1. 读取区间内 `template-v*` 的 Release 正文；
2. 运行同步；
3. 按语义解决冲突，`-Complete`；
4. 运行 `verify`；
5. 汇报未自动合并的项。

**升级闸门**：从第二个模板版本起成为模板发布的前置条件。

- 用上一个已发布版本生成 `identity-all-features` 和 `resource-capabilities-03`。
- 在其上分别提交以下项目定制：一个新增 npm 依赖（纯 API 场景改为 NuGet 依赖）、一处源码修改、一个替换的图片。
- 同步到候选版本。

**验收（三类分开）**

1. 无冲突时自动合并，`verify` 通过，定制全部保留。
2. 与候选在同一处冲突时，退出 2，保留双方内容，标记未更新。
3. 解决冲突并 `-Complete` 后，`verify` 通过。

另外，以下情况都要以明确诊断失败：缺少标记、旧参数不兼容、同版本重建出现差异。

- `-Complete` 反例：只有二进制冲突，或只有删除/修改冲突（均没有文本标记）时，直接 `-Complete` 必须拒绝；逐项解决后，在未提交的工作区里可以完成，之后 `verify` 通过。
- 存量接入：选一个首个模板包发布之前的真实 SHA，接入一个带源码和规范定制的无标记项目。接入完成后移走临时源码目录，再同步到下一个模板版本，定制仍然保留，`verify` 通过。

### P5 Skill、规范与 E2E

1. **压低生成项目的读取成本，并接入闸门。**
   - 对 `measure-template-read-cost.py` 实际统计的必读集合（`template/docs/standards/*`、项目 Skill 及 reference、Spartan、`template/docs/README.md`）做去重和按任务拆分，同步更新项目索引与统计模型。不能靠少登记必读文件来达标。
   - 五类任务全部低于上限后，把 `--check` 加入 `check-all.ps1`。
   - 根目录质量文档的精简另作为独立目标：CI 机制细节移入脚本头注释与自测，这一项不计入读取成本的下降。
2. **生成项目 E2E。**
   - 模板新增 Playwright E2E 工程，覆盖 `testing.md` 第 4 节列出的核心闭环。
   - 大多数用例用 API 预置登录（`storageState`）；至少一个用例真正走登录界面。
   - 使用 role/label 定位器，失败时保留 trace。
   - 范围按 `ServiceRole` 和 `SpaFrontend` 决定，纯 API 产品不要求浏览器 E2E。
   - `verify.ps1` 增加对应步骤；GitHub 与 GitLab 的 CI 薄壳都要准备浏览器依赖，并上传 trace。
3. **仓库的 OIDC 浏览器场景**从有头 agent-browser 迁到无头 Playwright，并纳入 full 档，并启用 P2a 责任表中的“OIDC 浏览器闭环”一行（只维护这一张表）。迁移时按删除验收规则逐项接替原有断言；验收要求省略浏览器阶段时放行汇总失败。
4. **Spartan Skill 单一来源**：仓库根的副本改为指向 `template/.agents/skills/spartan` 的软链接；`validate-skills.ps1` 拒绝内容不同的副本。
5. **Skill 文本**
   - 仓库专项 Skill 链接 P1 的维护配方。
   - 项目 Skill 链接随项目分发的测试规范。
   - 补充证据复用与后台等待两条规则，并按 `collaboration-scenarios.md` §6 做触发验收。

**验收**

- 五类读取成本全部不超过上限；`check-all` 能拦截超限。
- `identity-all-features` 的 `verify` 实际执行的 E2E 用例数 > 0；纯 API 产品没有浏览器 E2E 步骤。
- 注入一个权限显示缺陷，E2E 失败。

### 实施期间的收敛（2026-10-19 用户确认）

按评审取舍原则，对已经实施的部分做了收敛：
- 三方同步改用 git 原生的三方应用和未合并状态，不再自写合并引擎。同步时不再重复检测确定性，这项检测只放在模板发布闸门。
- 候选包身份只保留唯一预发布版本号、`packageSourceMapping` 和版本核对。
- 模板开发工作区去掉锁与归属机制。
- 发布物不出 SBOM，只保留来源证明。
- 成本统计脚本不入库，合入后观测时临时使用。
- P5.3（OIDC 浏览器迁移）本轮不做。
- P5.2（生成项目 E2E）只做最小骨架：登录、一个权限差异、一个 CRUD。

## 5. 评审重点

- **P2b 选择器**漏掉嵌套条件或 computed 依赖时，PR 会放进本可选中场景的缺陷。由 P2b 的反例夹具锁定，汇总独立重算。
- **P2a 放行汇总**若接受“不适用”或“用例数为 0”，放行候选就可能缺少整项责任。由 P2a 的反例锁定。
- **候选版本后缀**不得影响发布通道的版本推算与 `PackageReleaseNotes`。`scripts/test-workflow-change-scope.py` 要覆盖带后缀的本地包。
- **工作区 `-Sync`** 必须删除模板中已删的文件，且不覆盖未同步的修改。由 P1 的验收锁定。
- **同步**不得覆盖项目定制（依赖、二进制文件、源码）；有冲突时不得更新标记。由 P4b 的三类验收锁定。

## 6. 稳定文档的去向

| 入口 | 更新内容 |
| --- | --- |
| `docs/framework/quality-assurance.md` | 档位、放行责任表、PR 增量保证、本地维护配方、证据复用；替换“最多三组”与缓存约束 |
| `docs/framework/versioning.md` | 候选版本、SBOM 与来源证明、Dependabot、模板通道、授权清单 |
| `docs/template/quality-assurance.md` | core/full、生成拆片、升级闸门 |
| `docs/template/development-guide.md` | §3.5 档位与覆盖口径；§10 中的工作区、`verify -Fast` 与 run 目录 |
| `docs/architecture/collaboration-scenarios.md` | §2 规范归属；§4.3 中的工作区与同步链路 |
| `docs/README.md` | 分发边界 |
| 生成项目的 `testing.md`、`docs/README.md`，以及项目 Skill | `-Fast`、E2E、同步意图、`docs/project/` |
