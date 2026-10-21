# 仓库架构

本目录保存跨 `framework`、`template`、Skill 和文档体系长期有效的架构原则。这里的内容是当前约束，不是某次改造的过程记录。

| 文档 | 作用 |
| --- | --- |
| [设计原则](./design-principles.md) | 三层边界、文档、Skill 与验证的长期约束 |
| [三层交付与 AI 协作](./collaboration-scenarios.md) | Framework、Skills、Template 的场景、步骤与文档读写链路 |
| [前端组件体系](./frontend-ui-library.md) | 模板前端的技术组成、依据与维护约束 |
| [隔离与授权场景](./isolation-and-authorization-scenarios.md) | 多租户、功能权限、数据范围的适用边界、组合顺序与各自的验证策略 |
| [操作记录长期约束](./operation-records-principles.md) | 记录粒度、语言与快照、注解安全边界、可见性分层、归档与输出取舍 |

具体框架维护规范位于 [`docs/framework/`](../framework/README.md)，模板维护规范位于 [`docs/template/`](../template/README.md)。规格文档（需求、方案、计划与结果）的归属与生命周期见[仓库维护文档](../README.md#规格文档)。
