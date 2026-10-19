---
name: maintaining-leistd-repository
description: 在 leistd-net 仓库中处理跨 framework、template、skills、docs、CI、版本或发布流程的变更时使用，也适用于“梳理仓库架构”“同步框架与模板”“调整 Skill 或文档体系”等请求。负责统筹多个交付面及其验证证据；单独修改 framework 或 template 应使用对应专项 Skill，不用于生成后的业务项目开发或仅查询 Leistd NuGet 用法。
---

# 维护 leistd-net 仓库

## 先确定交付面

涉及跨层边界时读取 `docs/architecture/collaboration-scenarios.md`，再按变更范围选择：

| 范围 | 同时使用 | 主要事实 |
| --- | --- | --- |
| `framework/` | `developing-leistd-framework` | 源码、测试、`framework/docs/`、`docs/framework/` |
| `template/` | `developing-leistd-template` | `template.json`、模板源码、实际生成结果 |
| 任一 Skill | 官方 `skill-creator` | `SKILL.md`、所属交付面的当前事实 |
| CI、根文档或版本发布 | 本 Skill | workflow、脚本、`docs/` 稳定入口、候选 SHA 和包源状态 |

跨多个交付面且需要任务分解时使用一份实施计划，分别完成受影响层的验证。

注释与文档按所属交付面读取[框架规范](../../../docs/framework/development-guide.md#4-文档注释)或[项目通用规范](../../../template/docs/standards/coding-common.md#1-语言与敏感信息)，前端再读[前端规范](../../../template/docs/standards/coding-frontend.md#3-命名)。规则只维护在这些入口，Skill 不复制。

## 方案与实施计划

评估、计划、报告与稳定规范的归属和生命周期，以及 `framework/docs/`、`template/` 两个分发载荷不得写入的仓库信息，以 [`docs/README.md`](../../../docs/README.md) 为准。

## 工作流

1. 检查分支、工作区和用户已有改动，确认当前变更基线。
2. 读取相关稳定规范、邻近实现和最新同类文档，不把历史 assessment 或 plan 当作当前规则。
3. 确认每项事实的唯一维护位置，按交付面加载并应用对应专项 Skill。
4. 实施聚焦变更，并分别完成构建、测试、文档检查、打包或模板生成验证。
5. 汇总各交付面的真实结果、未验证项和残余风险；只有长期共享信息才更新稳定文档。

要求 Git 提交或推送时只暂存本任务文件，核对相关验证、`docs/framework/versioning.md` 的提交格式及目标分支触发的 workflow；不把实现完成当作已经提交或发布。等待具体 CI run 按[证据复用](../../../docs/framework/quality-assurance.md#证据复用)的后台等待规则，期间推进不依赖它的工作；不按固定时长等待，也不重复运行已成功的相同验证。

实际发布或恢复时，按 `docs/framework/versioning.md` 和 `.github/workflows/release.yml` 核对候选 SHA、版本、同一 SHA 的质量结果、包源、tag 与 Release；部分发布先查实际状态，再按版本规范恢复。

没有同类文档且信息需跨会话复用时，按 `docs/README.md` 创建最小权威文档；归属或关键决策不明确时询问用户。

发布正式包、生产操作、真实数据修改、密钥变更和破坏性 Git 操作前核对现有授权是否覆盖目标与动作；未授权或范围扩大时再询问。

## 验证入口

各交付面的本地验证按[本地验证配方](../../../docs/framework/quality-assurance.md#本地验证配方)选择，已有证据是否仍然有效按[证据复用](../../../docs/framework/quality-assurance.md#证据复用)判断。
