---
name: developing-leistd-template
description: 在 leistd-net 仓库中为 template 的后端、前端、条件参数、生成项目文档、项目 Skill、Mock 或部署资产设计方案、制定实施计划、修改或审查时使用，也用于适配 Leistd 公共 API 并验证生成场景。不能替代框架组件开发；生成后的业务功能开发应使用项目内 Skill。
---

# 开发 Fullstack App Template

## 建立事实

从目标文件和直接依赖建立事实，先参考同类实现和测试；关键约束再核对相关规范章节。涉及框架用法时核对实际消费包及随包文档，框架源码或契约变化时再读对应源码。生成后的业务开发使用项目内 Skill。

| 场景 | 补充事实与规范 | 验证重点 |
| --- | --- | --- |
| 后端或前端骨架 | 同类实现、测试与 `template/docs/standards/` 对应章节；分页排序读 [API 规范 §5](../../../template/docs/standards/api.md#5-分页规范)，业务验证器读 [后端规范 §5](../../../template/docs/standards/coding-backend.md#5-命名与-dto)；Spartan 组件用专项 Skill | 受影响场景生成、构建、测试；交互变化做浏览器验证 |
| 参数、条件、文件裁剪 | `template.json`、相邻条件块、`docs/template/development-guide.md` | 按质量规范区分局部源条件与全局生成输入，验证实际产物 |
| 架构边界、权限、租户、数据库 | 调用链、Migrator、`docs/architecture/design-principles.md` 及相关后端规范 | 真实 PostgreSQL 与受影响生成场景 |
| Dockerfile、Compose、部署入口 | 实际容器配置、`template/docs/deploy/README.md` | 镜像可构建、.NET 运行时层可用；实际启动另验 |
| 缺陷修复 | 复现所需的生成场景、相关测试 | 在生成项目中先写修复前失败的回归测试，回归测试与修复一起写回模板源后重新生成受影响场景验证 |
| 项目 Skill 或规范 | 生成项目入口、官方 `skill-creator`、`template/docs/README.md` | Skill 校验和代表性生成结果 |
| 模板包、参数增删、来源标记或模板同步 | `template-package/`、`template/.leistd/template.json`、[模板包与来源标记](../../../docs/template/quality-assurance.md#模板包与来源标记) | `pwsh template-package/test-template-package.ps1`：包内容、隔离安装、标记与重建零差异；改 `template/scripts/sync-template.ps1`、标记库或升级闸门时 `pwsh template-package/test-template-sync.ps1` |

模板通过 `PackageReference` 消费框架；`Api` 是组合根，Application 不依赖 Infrastructure。模板源可编辑不等于交付完成，必须验证实际生成结果。

模板源码不产随包 XML。注释先读[通用规范](../../../template/docs/standards/coding-common.md#1-语言与敏感信息)，XML 格式读[后端规范](../../../template/docs/standards/coding-backend.md#文档注释)，JSDoc 读[前端规范](../../../template/docs/standards/coding-frontend.md#3-命名)。

## 方案与实施计划

涉及 Template 技术选型、结构调整、参数迁移或跨场景改造时，先搜索同主题最新文档；评估、计划与稳定规范的归属和生命周期见 [`docs/README.md`](../../../docs/README.md)。即使计划只影响 Template，也只写在根 `docs/`。

## 对外分发边界

`template/` 是 `Leistd.Templates` 模板包（`dotnet new`）的对外发布载荷，只包含生成项目运行和持续开发真正需要的内容：当前源码、配置、测试、Mock、部署资产、工程规范和项目级 Skill。哪些仓库信息不得进入分发载荷见 [`docs/README.md` 分发边界](../../../docs/README.md#分发边界)。

`template/.agents/skills/leistd-project-workflow/` 只描述生成后业务项目自身的分析、实现、审查、测试和部署协作，不承载 leistd-net 的 Framework/Template 维护流程。

## 工作流

1. 检查分支、工作区和用户已有改动，列出受影响参数、条件组合、替换项和排除文件。
2. 需要某种基础能力（锁、缓存、事件、本地化、审计等）时，先读 `framework/docs/components/` 下对应组件的文档，
   按它给出的入口接入；不要只看实现源码就自行判断，更不要在模板里另建平行抽象。
   组件文档写明的部署前提（例如分布式锁要求多副本部署配置 Redis）属于契约，不是待修的缺陷；
   确有缺口就反馈到框架组件，由那一层收口。
3. 沿用最新同类模板实现，修改源码、测试和确有长期价值的文档。
4. 框架包内容变化或无法确认现有包对应当前源码时，打包到 `.tmp/local-feed`；否则复用已验证的包，经 NuGet 路径消费。
5. 在[模板开发工作区](../../../docs/framework/quality-assurance.md#模板开发工作区)里同步受影响场景，检查占位符、条件裁剪与格式；只改文案时不重复运行无关测试。
6. 代码或配置变化时，按下方验证入口在工作区里验证。
7. 权限、审计、通知、实时或数据库变化时补充对应业务闭环验证。

用户要求提交时只暂存本任务文件，核对生成场景与验证结果；提交格式按 `docs/framework/versioning.md` 的相关章节执行。仓库版本交付由 `maintaining-leistd-repository` 负责；Template 维护完成不等于生成项目已部署。

维护规则归 `docs/template/`，项目协作流程归 `template/.agents/skills/leistd-project-workflow/`，生成项目的长期事实由 `template/docs/README.md` 索引；三者分工见 `docs/architecture/collaboration-scenarios.md` §2。改 `template/docs/` 时同步索引的按任务读取表，并按 `docs/template/development-guide.md` §4 控制核心规范篇幅。只沉淀已验证、需复用的信息。

## 验证入口

本地只产出定向证据，按[本地验证配方](../../../docs/framework/quality-assurance.md#本地验证配方)选择，重复验证与等待 CI 按[证据复用](../../../docs/framework/quality-assurance.md#证据复用)；多场景完整阶段与全集由 CI 承担，矩阵失败用对应场景复现。未执行的场景、档位和风险必须说明。
