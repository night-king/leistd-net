# 质量检查与本地验证

本文定义 leistd-net 开发时的验证档位、本地配方、证据复用、审查与删除验收，不随 NuGet 分发。

- 框架用例的目录、注册契约与覆盖取舍见[开发规范 §7](./development-guide.md#7-测试)。
- 模板的测试层级、矩阵与 lint 缓存见[模板质量验证](../template/quality-assurance.md)，模板开发工作区见[模板开发规范 §5](../template/development-guide.md#5-模板开发工作区)。
- CI 的输入分类、同候选计划、回执与放行责任见[CI 质量计划与放行](./ci-release-gates.md)，维护 CI 时才需要读。

## 分层执行与时间预算

按变化行为和依赖选择验证，不在每次编辑后跑全量。

**本地只产出定向证据**：用目标测试和必要的真实集成定位并证明本次改动。这条规则只在本节维护，Skill 与其他文档链接这里。

- PR CI 只承担[增量保证](../template/quality-assurance.md#pr-增量保证)；全局条件行覆盖、两两组合与模板全集由合入后与发布前的 full 证明。
- 本地全量矩阵只用于复现 CI 失败（`-Scenarios <失败场景>`）。
- 生成后的业务项目按自身[测试规范](../../template/docs/standards/testing.md)执行，未配置必过 CI 时不能把完整责任交给 CI。

| 档位 | 时机 | 预算 | 内容 |
| --- | --- | --- | --- |
| L0 编辑循环 | 每次小改动 | ≤ 1 分钟 | 受影响项目构建 + `dotnet test <测试项目> --filter "FullyQualifiedName~<类>"`；改了某道闸门就用 `check-all.ps1 -Only <闸门名>` 单跑它 |
| L1 阶段完成 / 推送前 | 完整改动形成后 | 框架与模板的局部改动在工作区已预热时中位 ≤ 5 分钟 | 下表配方给出的定向检查；不为预算跳过配方里的检查 |
| L2 PR CI | 每次推送 PR | 墙钟 ≤ 8.5 分钟 | 完整静态闸门；依据[同候选输入计划](./ci-release-gates.md#同候选输入计划)执行必要框架测试、包内容/消费、登记路径命中时的 PostgreSQL/OIDC，以及 `core` 档的增量保证；未知、共享或无法证明的输入跑代表场景冒烟，不退回 `full` |
| L3 全集 | 代码/共享输入推送与实际发布；已验证基线的纯文档推送按[文档例外](./ci-release-gates.md#pr-的内部文档例外)执行 | 不设严格预算 | 同 L2，模板跑 `full` 档全部场景；发布等待同候选的 L3 结果，stable 可按同树规则承接 develop 证据；develop 与最高正式版同树且该 tag 是候选祖先时，不产生新内容与发布物，整体跳过质量 |

### 本地验证配方

L1 按改动选择配方。仓库维护的配方只在这里维护，仓库 Skill 链接本节，不另抄一份。

| 场景 | 本地做 | 本地不做（交给 CI） |
| --- | --- | --- |
| 框架组件实现 | 构建，加[选择器](#框架与真实依赖)给出的受影响测试项目；公共 API 或包依赖变化时打包，并做 `-PackageIds` 隔离消费 | 模板矩阵、PG/OIDC 端到端 |
| 模板后端局部源码 | [工作区](../template/development-guide.md#5-模板开发工作区) `-Sync`，加目标测试类；数据库或租户相关时加目标集成测试类 | 多场景完整阶段 |
| 模板前端局部源码 | 工作区 `-Sync`，加目标 spec 和 lint 缓存；交互变化时在 Mock 站点做浏览器验证 | 全量 spec、多场景 |
| 条件块、`template.json` | 用 [PR 选择器](../template/quality-assurance.md#pr-增量保证)算出场景，`-GenerateOnly` 加格式与链接检查；挑一个场景在工作区里构建 | 完整的 full 档 |
| CI 与闸门脚本 | 被改脚本的自检与夹具 | 远端调度验收之外的全量 |
| 内部文档、Skill | `check-all -Only` 相关闸门 | 其余 |

- **闸门**：`scripts/check-all.ps1 -Only '<闸门名>,<闸门名>'` 只跑所选闸门，名称以 `-List` 的输出为准。完整的 `check-all.ps1` 由 CI 的静态作业承担；本地需要时也可整体运行，外部闸门并行执行，本机实测约 40 秒（串行约 85 秒）。
- **随包文档**：`framework/docs/` 的改动加打包内容检查；`template/docs/` 与项目 Skill 的改动加 `test-template-matrix.ps1 -Scenarios <场景> -GenerateOnly` 的生成产物检查（文件集合、条件裁剪、文档引用与章节锚点）。影响运行契约时按上表对应的源码行执行。
- **条件块与 `template.json`**：提交改动后运行选择器，再只生成它给出的场景。`Selection` 的取值：
  - `selector`：PR 会执行的场景（生成文档的条件分支也计入）；
  - `representative`：改动无法收窄（如 `template.json` 变化），清单与 PR 相同，是四个代表场景加可证明的分支补选，其余场景由合入后的 full 生成；
  - `full`：只出现在脏树或选择期间 HEAD 变化时，先形成提交再选择。

  ```powershell
  $taskBase = git merge-base origin/develop HEAD
  python3 scripts/plan-quality-checks.py --local-scenarios --tier core --base $taskBase --output .tmp/local-scenarios.json
  $selection = Get-Content .tmp/local-scenarios.json -Raw | ConvertFrom-Json
  pwsh scripts/test-template-matrix.ps1 -Scenarios $selection.Scenarios -GenerateOnly
  ```

- **被改脚本的自检**（影响调度时用远端 CI 验收）：

  | 改动 | 运行 |
  | --- | --- |
  | 范围/聚合、框架测试回执或发布链接（`PackageReleaseNotes` 与可选升级指南链接） | `python scripts/test-workflow-change-scope.py`；只改发布推送、产物保存或恢复指引时可加 `--release-publish` 只跑该部分（不需要 .NET） |
  | 选测/回执 | `python scripts/test-quality-validation-plan.py` |
  | 预检接替 | `python scripts/test-template-source-preflight.py` |
  | 候选包源、一次性 NuGet 配置或版本覆盖 | `pwsh framework/build/test-candidate-feed.ps1`（真实 restore 的正反例，不访问网络） |
  | 模板开发工作区 | `pwsh scripts/test-template-dev-workspace.ps1` |
  | 生成项目 `verify.ps1` 的步骤或模式 | 用一个场景的 `-GenerateOnly` 核对 `verify -List` |

### 本地不做

- 不在本地跑 `-Tier core`、`full` 档或不带参数的全量矩阵；复现 CI 失败时只跑失败的场景（`-Scenarios <失败场景>`）。
- 不为局部改动执行多场景完整阶段、全量 spec 或 PostgreSQL/OIDC 端到端，这些由 PR CI 按同候选计划承担。

### 证据复用

- 证据仍然有效、也没有新的验证需要时，不重复执行相同的验证。源码、依赖、fixture、平台或安全数据源（如审计库）变化后需要重验。推送之后，不在本地重复 CI 已承担的完整责任。
- 等待 CI 时，在后台运行 `gh run watch <id> --exit-status`，靠完成通知唤醒，不写 sleep 轮询循环。
- 未执行的档位与入口须在交付说明里列出。
- stable 的自动同树复用由[CI 规则](./ci-release-gates.md#正式发布复用同树质量证据)验证来源，不以人工或 AI 自证替代。修改该入口须验证复用、回退、实时审计失败及错误见证的拒绝路径；真实 API 只读演练区分真实来源与模拟候选，首次 stable 前补足合格来源正例。

### 时间边界与等待

L0–L2 不以真实时间流逝等待安全有效期（锁定、挑战、令牌寿命、缓存寿命、限频窗口）来验证时间边界。

- 应用控制的判据用官方 `FakeTimeProvider` 或显式时刻在单元、集成测试里验证；端到端只验接线与生效值。
- 跨进程的真实到期只放在 L3：OIDC 端到端的 `-IncludeExpiryWait` 由 `full` 档传入，以快速档（`OAuth__AccessTokenLifetime=00:01:30`）执行撤销到期与交换令牌到期，并从签发的令牌断言快速档已生效。
- 实际 I/O、同步、取消与超时用有上限且观察目标完成的等待，不在此列。
- 生成项目的同一原则见模板 [`testing.md`](../../template/docs/standards/testing.md) §2.2。

第三方 SDK 不提供可注入时间时，重复的故障场景可在测试宿主通过官方配置缩短传输等待；默认生产超时由独立组合契约验证，并保留一个真实超时行为用例。

- 共享异步工作使用开始／释放信号与显式取消，保护性超时只拒绝挂起。
- 入口观察不能冒称 SDK 内部等待者观察，仍需取消隔离、共享结果、请求次数和故障反例证据。
- 测试缓存 TTL 时，计算与实际本地缓存到期共用同一官方假时钟，不能只快进一层或主动删除条目。

### 框架与真实依赖

框架 L0 可收窄到目标类或家族；L1 按清单运行受影响测试项目：与 CI 同一选择器，从任务基线到已提交 HEAD 的差异求改动项目的反向依赖闭包，覆盖其他家族的反向依赖。

```powershell
$taskBase = git merge-base origin/develop HEAD
python3 scripts/plan-quality-checks.py --local-framework-tests --tier core --base $taskBase --output .tmp/local-framework-tests.json
foreach ($project in (Get-Content .tmp/local-framework-tests.json -Raw | ConvertFrom-Json).Projects) {
    dotnet test $project -c Release
    if ($LASTEXITCODE -ne 0) { throw "$project 失败" }
}
```

`Selection` 为 `all` 时清单即全集，`Reason` 说明退回原因；脏树同样退回全集，先形成提交再选择。全集（`dotnet test framework/Leistd.Framework.slnx -c Release`）留给 L3 与无法证明的输入。

测试项目包含关系型 Provider、TestServer 和真实 Redis 契约，不全是纯单测。本地缺 Redis 时按现有规则显式跳过并列为未执行；变化涉及锁实现或 Redis 接线时，必须在可达环境验证相关契约。无关的纯内存规则不因此启动 Redis。

纯领域拒绝或参数判断用单测；数据库事务、HTTP 授权、协议和网络取消等依赖协作契约用目标真实集成。CI 承担完整回归，不意味着已知失败要等到 CI 才第一次复现。`--no-build` 必须先构建本次源码。

### 审查与失败后的重跑

默认不审查，交付时提醒用户可以审查；影响公共 API、发布或安全的改动一定提醒。用户要求审查时，由用户指定的审查方执行（任何 agent 或人，不限于 Codex），可与 PR CI 并行。结论必须明确写“通过”或“不通过”并列出问题；不通过就修，修完再审，直到通过，不限轮数。

失败时只修失败点，只重跑失败点和直接受影响的检查，不整条链重跑。矩阵失败用 `-Scenarios <失败场景>` 定位，不靠猜测反复推送。审查方复核有效证据，不因更换审查方重跑无关全套；相关代码、配置、依赖、fixture 或平台变化使原证据失效。

审查覆盖最终 head 与基线，CI 核对实际执行的 merge 候选及其 head/base 关系；不能机械要求二者 SHA 相同。base 更新后重验融合候选，发布使用最终合入 SHA。本地结果记录命令、范围、环境、实际结果及 SHA/未提交 diff 摘要；跳过不等于通过，Mac 结果不替代 Linux CI。

规则依据：[Anthropic 长任务应用开发](https://www.anthropic.com/engineering/harness-design-long-running-apps)、[Claude Code 最佳实践](https://code.claude.com/docs/en/best-practices)。验证围绕可观察完成条件与真实结果，不固定增加评审代理或逐阶段会签。

## 定时任务的覆盖分工

通用调度行为由 BackgroundJobs 组件集中验证：真实 `RecurringJobScheduler` 配合官方 `FakeTimeProvider`、内存依赖与计时器登记/完成信号，覆盖每日排期的未到点、到点、下一轮、全局/单任务禁用、非法排期隔离、等待/执行中的停止。先观察后台初始化完成再推进时钟；停止后同时核对执行任务正常完成与没有新增运行。真实周期等待不加入编辑或提交入口，保护性超时只用于拒绝卡死。

Job 的集群锁、水位和失败重试仍由执行器契约测试承担。模板和业务项目只补自身业务判据、失败传播以及真实持久化/租户/DI 接线，不对每个 Job 复制通用调度循环测试。服务成功启动不能替代定向行为断言；新增测试复用现有测试项目与 CI，不另建定时维护流水线。

## 删除与替换的验收

删除或下沉普通测试时，指出已覆盖同一行为的测试即可。宿主启动或正常 HTTP 请求成功不算覆盖纯规则边界、注册生命周期与幂等、关系型约束和失败路径。

删除或替换闸门或安全判据（`check-all` 闸门、CI 计划与回执判据、认证授权与租户隔离的拒绝断言）时，说明它原来能抓什么缺陷、由哪个入口接替，没有接替者就保留。

- 接替关系用变异验证证明：在隔离输入中逐项注回旧链能发现的缺陷，记录旧入口和新入口的非零退出码与实际诊断；合法对照与撤销注入后的入口须绿。
- 缓存或调度替换还须验证新增文件、配置/依赖变化、缺失/取消的检查不会产生假绿。
- 仅自检全绿、覆盖率或推理不足以验收删除。最终入口直接使用接替者，不留新旧检查并行的过渡模式或迁移开关。

运行时或测试接替的变异须可编译，先确认构建成功，再确认指定断言失败。`if (false)` / `if (true)` 引发的 CS0162 或其他编译失败不能证明测试接替有效；只有接替者本身是编译器/分析器时，对应编译诊断才是验收证据。共享工作树中的验证可能依赖其他会话未提交的修复，提交前还须验证实际候选快照，不能用工作树全绿代替候选全绿。
