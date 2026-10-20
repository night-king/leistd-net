# 框架开发文档

本目录只保存 leistd-net 仓库内部的框架开发与发布规范，不随 `Leistd.*` NuGet 包分发。

| 目标 | 文档 |
| --- | --- |
| 新增或修改框架组件 | [框架开发规范](./development-guide.md) |
| 选择本地验证、复用证据、审查与证明删除接替关系 | [质量检查与本地验证](./quality-assurance.md) |
| 维护 CI 的输入分类、同候选计划、回执与放行责任 | [CI 质量计划与放行](./ci-release-gates.md) |
| 理解分支与 PR、版本、发布通道与发布门禁 | [版本与发布规范](./versioning.md) |

面向 NuGet 使用者的组件与 DDD 基座文档位于 [`framework/docs/`](../../framework/docs/README.md)。开发框架时同时使用 [`developing-leistd-framework`](../../.agents/skills/developing-leistd-framework/SKILL.md)，按源码核对 API 与运行时行为。
