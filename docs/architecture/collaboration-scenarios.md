# 三层交付与 AI 协作

本文说明 Framework、Skills、Template 的使用场景和端到端链路。长期约束见 [设计原则](./design-principles.md)，具体行为以源码、配置和所属层文档为准。

## 1. 三层交付面

| 层 | 交付内容 | 主要使用者 | 完成标准 |
| --- | --- | --- | --- |
| Framework | `framework/components/`、`framework/ddd-struct/` 和 NuGet 包 | 框架维护者、业务开发者 | 包可独立消费，行为、测试和随包文档一致 |
| Skills | 仓库维护、框架使用和项目协作 Skill | 执行对应任务的 AI | 准确触发，只加载必要事实并完成用户最终意图 |
| Template | `template/` 的后端、前端、规范和条件参数 | 新项目团队、模板维护者 | 条件场景可生成、构建、运行并消费 NuGet 包 |

三层不是运行时依赖链。Framework 提供能力，Skills 提供执行知识，Template 组合并验证能力；任何一层都不复制其他层的权威事实。

## 2. Skill 边界

| Skill | 场景 | 事实范围 |
| --- | --- | --- |
| `.agents/skills/developing-leistd-framework` | 开发 Framework | 框架源码、测试、随包文档和包验证 |
| `.agents/skills/developing-leistd-template` | 维护 Template | 模板源码、条件配置和实际生成结果 |
| `.agents/skills/maintaining-leistd-repository` | 跨层、CI、仓库规则及版本发布 | 整个仓库、候选 SHA、包源及各专项 Skill 的验证结果 |
| `skills/leistd-net-framework` | 下游项目使用 `Leistd.*` | 项目实际安装包的文档、XML 和程序集 |
| `template/.agents/skills/leistd-project-workflow` | 生成项目的开发、协作与环境交付 | 项目源码、配置、测试、CI、部署事实和已沉淀文档 |

维护 Skill 时同时使用官方 `skill-creator`。每个 Skill 必须在自己的分发环境中自洽，不能依赖不会一同交付的文件。

生成项目内的分工（路径为模板源，生成后去掉 `template/` 前缀）：

| 载体 | 内容 | 维护者 |
| --- | --- | --- |
| `template/.agents/skills/leistd-project-workflow` | 与语言、技术栈无关的开发流程、通用原则和规范演进机制 | 模板维护者；生成后项目通常不改 |
| `template/docs/standards/` | 模板的 .NET/Angular 工程规范基线 | 模板维护者；随模板版本整体更新，项目不改写 |
| 生成项目的 docs/project/ | 项目自己的规则，与基线冲突时以它为准（优先级写在 `template/docs/README.md`） | 项目，由 Skill 驱动积累与精简；首次需要时创建，模板不预建 |
| `template/docs/README.md` | 唯一文档索引与按任务读取表 | 项目（登记 docs/project/ 等按需文档） |
| `template/AGENTS.md`、`template/CLAUDE.md` | 只指向 Skill 与项目文档索引 | 基本不变 |
| `template/.leistd/template.json` | 来源标记：模板包版本与源提交、生成名称与全部参数、各项目 UserSecretsId、SDK | `dotnet new` 写入，不手工修改 |

模板以 `Leistd.Templates` 包按 `template-vX.Y.Z` 版本发布，生成项目带来源标记，同一版本、相同输入可以重建出与原产物零差异的基线。项目新增的规则写入生成项目的 docs/project/，不改写 `template/docs/standards/` 生成的基线，也不回流到通用 Skill；这样基线能按模板版本整体替换，而项目规则不受影响。

## 3. 事实入口

| 场景 | 稳定入口 | 按需事实 |
| --- | --- | --- |
| 框架设计与维护 | `docs/architecture/design-principles.md`、`docs/framework/development-guide.md` | 目标源码、测试、`.csproj`、`framework/docs/` |
| 版本与发布 | `docs/framework/versioning.md` | `release.yml`、`VERSION`、包源与 tag 状态 |
| 框架组件使用 | `leistd-net-framework`；仓库内入口 `framework/docs/README.md` | 已安装 NuGet 包的 `docs/*.md`、XML、项目配置 |
| 模板维护 | `docs/template/development-guide.md` | `template.json`、模板源码、生成场景 |
| 业务项目协作 | `AGENTS.md` → `leistd-project-workflow`、项目 `docs/README.md`（模板源：`template/.agents/skills/leistd-project-workflow/`、`template/docs/README.md`） | 项目源码、配置、测试、CI 和最新同类文档 |

历史 assessment、plan 和 Git 记录用于追溯，不作为当前规则入口。

## 4. 端到端链路

### 4.1 下游项目使用框架

1. 从项目文件、CPM 和还原资产确认宿主、目标框架及实际 `Leistd.*` 版本。
2. 由 `leistd-net-framework` 定位该版本包内文档和 XML，不从在线最新版或模型记忆猜 API。
3. 按项目现有边界完成注册、Options、端点、Store、DbContext 或迁移集成。
4. 构建并运行相关测试；需要运行时语义时启动真实宿主，验证主要路径和拒绝路径。
5. 仅将项目特有且长期复用的决策写入项目文档，框架事实继续引用包内文档。

### 4.2 开发 Framework

1. 从目标文件和直接依赖建立事实；涉及组件边界与公共 API 时再读取相关设计原则、框架规范和同类实现。
2. 修改源码和风险匹配的测试，核对依赖方向及当前消费者。
3. 同步受影响的 XML 注释和 `framework/docs/` 使用者文档。
4. 按变更类型构建和测试；随包内容或公共契约变化时打包到 `.tmp/local-feed`。
5. 包依赖或集成契约变化时，从隔离本地源完成 NuGet 消费验证。
6. Template 消费该能力时继续验证对应生成场景；否则使用组件集成测试或临时宿主闭环。

### 4.3 维护 Template

1. 从目标文件和直接依赖建立事实；条件生成变化时读取模板规范、`template.json` 和邻近条件块，确定受影响参数组合。
2. 同步修改模板源码、测试、Mock、项目 Skill 和确有长期价值的生成项目文档。
3. 框架包内容变化或无法确认现有包对应当前源码时生成本地包，否则复用已验证的包；通过 `PackageReference` 消费。
4. 用 `scripts/template-dev-workspace.ps1 -Scenario <场景>` 打开模板开发工作区：首次运行把场景生成到 `.tmp/dev/<场景>/project/`，完成 restore 与 `npm ci`。
5. 每次改模板源码后 `-Sync`，在工作区里运行目标测试类、目标 spec、lint 或 `verify -Fast`；修复始终写在模板源，工作区有未同步的修改时 `-Sync` 停下列出，不覆盖。
6. 权限、审计、通知、实时或数据库变化时完成对应业务闭环。
7. 多场景完整阶段与条件组合交给 CI，本地按[本地验证配方](../framework/quality-assurance.md#本地验证配方)只产出定向证据。
8. 新增或改名模板参数时同步来源标记的派生符号与字段；改模板包内容或打包规则时运行 `pwsh template-package/test-template-package.ps1`，改同步脚本、标记库或升级闸门时运行 `pwsh template-package/test-template-sync.ps1`（[模板包与来源标记](../template/quality-assurance.md#模板包与来源标记)）。合入 `main` 后，自上一个 `template-v*` tag 起模板有变化即由 template 通道发布新的模板版本，变更说明是该 tag 的 Release 正文（[模板包发布](../framework/versioning.md#模板包发布template-通道)）。

### 4.4 业务项目协作

1. `leistd-project-workflow` 根据用户最终意图确定交付结果，而不是按固定阶段选择多个 Skill。
2. 从目标文件及直接依赖建立事实，按任务边界查阅配置、测试、CI、`docs/README.md` 和同类文档。
3. 按需加载 development、quality、delivery、documentation 或 bootstrap reference。
4. 完成方案、实现、审查、测试、部署或协调目标，并运行风险匹配的验证。
5. 仅在产生长期可复用信息时更新或创建最小权威文档；缺少文档不阻断低风险任务。
6. 汇报实际结果、未验证项和残余风险，不默认生成阶段报告。

### 4.5 跨层维护

跨 Framework、Template、Skills、CI、版本规则或实际发布的任务由 `maintaining-leistd-repository` 统筹：先确定受影响交付面，再由各专项 Skill 完成对应实现和验证。发布与恢复以版本规范和实际 workflow 为准。某层不受影响时明确核对后跳过，不制造空改动。

## 5. 生成项目意图

| 用户最终意图 | 完成条件 |
| --- | --- |
| 只要分析或方案 | 决策、风险、验收和步骤明确，并说明尚未实现 |
| 要求实现或修复 | 可观察行为、必要测试、运行验证和文档同步完成 |
| 只要代码审查 | findings、证据、未检查范围和残余风险明确 |
| 只要测试或定位故障 | 命令和结果可复现，原因与未覆盖范围明确 |
| 多任务或跨会话推进 | 每个完成条件都有真实产物和验证证据 |
| 部署、迁移或回滚 | 获准操作、健康检查、核心路径和回滚判断完成 |

辅助步骤不转移最终交付责任。不可逆、生产、真实数据、密钥变更、费用或流量操作需核对现有授权；未覆盖当前目标、环境或动作时再确认。

## 6. Skill 验收

静态校验只证明目录和元数据合法。修改 Skill 的 description、边界或流程后，还应在不泄露预期答案的上下文中验证：

| 请求 | 应触发 | 关键边界 |
| --- | --- | --- |
| “给 auditing 增加 Options 并补测试” | `developing-leistd-framework` | 不加载模板或仓库统筹 Skill |
| “调整 Authorization API，并同步模板和 CI” | `maintaining-leistd-repository` + 专项 Skill | 不把单层通过当作全部完成 |
| “在现有 ASP.NET Core 项目中接入 Notifications” | `leistd-net-framework` | 读取已安装版本，不猜 API |
| “在模板项目中实现订单管理” | `leistd-project-workflow` | 实现和验证完成前不宣称交付 |
| “只审查这个 PR” | `leistd-project-workflow` | findings 优先，不擅自扩大为实现 |
| “部署到生产并准备回滚” | `leistd-project-workflow` 的 delivery reference | 核对目标与现有授权，验证实际产物和迁移状态 |
| “发布 Framework beta” | `maintaining-leistd-repository` | 同一 SHA 的质量闸门、包源和 tag 验收 |
| “只修改组件文档的一处错误” | `developing-leistd-framework` | 检查引用与包内容，不运行无关数据库和前端测试 |
| “调整模板条件开关” | `developing-leistd-template` | 生成开关两侧，核对裁剪与链接 |
| “修改 Dockerfile 和部署变量” | `developing-leistd-template` | 生成项目镜像可构建、.NET 运行时层可用；实际启动另验 |
| “已授权部署到预发，继续完成” | `leistd-project-workflow` 的 delivery reference | 连续执行授权步骤，等待具体状态，不重复询问或固定休眠 |
| 在没有 `docs/README.md` 的其他技术栈项目中“修复这个 bug” | `leistd-project-workflow` | 从源码、配置和测试恢复约定继续，不因缺少索引停止 |
| 同一条审查意见第二次出现 | `leistd-project-workflow` 的 documentation reference | 沉淀为项目规范或闸门，并登记索引；不写入 Skill |
| 发现规范与实现冲突或重复 | `leistd-project-workflow` 的 documentation reference | 修正或删除过时条目，不新增平行规则 |
| “只审查这个 PR”时发现可沉淀的规则 | `leistd-project-workflow` | 只在结果中报告候选，不修改文件 |

流程提示不能可靠检测重复或过时规则；判据稳定且收益明确时使用闸门或测试，不继续加长 Skill。

对每个场景检查触发 Skill、实际读取的资料、交付结果、额外工作和等待次数；描述发生变化时也用相邻的非触发请求检验误匹配。验收结果保留在当前评审、PR 或 CI；只有团队或合规明确要求时才另建长期记录。
