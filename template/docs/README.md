# MyProject 文档

本目录保存需要长期复用的项目知识；源码、配置、测试、Git 和 CI 已能表达的不重复写成文档。

## 按任务读取

任务跨多类时取并集，未列出的任务按下方全部规范选读。

| 任务 | 必读 | 按需 |
| --- | --- | --- |
| 后端功能或修复 | [通用约定](standards/coding-common.md)、[后端](standards/coding-backend.md)、[测试](standards/testing.md) | 改接口、分页排序或输入验证读 [API](standards/api.md)；涉及登录、会话、权限或租户读 [认证与授权](standards/auth.md)；调用其他服务读 [服务间调用](standards/service-invocation.md) |
| 调整后端目录 | [后端目录规则 §2](standards/coding-backend.md#2-分层与目录)、[项目目录](standards/project-structure.md) | Api 读 [§8](standards/coding-backend.md#8-api-目录)，Client / DbMigrator 读 [§9](standards/coding-backend.md#9-client-与-dbmigrator-目录)；按影响面读取测试与相关专题 |
<!--#if (SpaFrontend)-->
<!--#if (IncludeLocalization)-->
| 前端功能或修复 | [通用约定](standards/coding-common.md)、[前端](standards/coding-frontend.md)、[测试](standards/testing.md) | 改界面、表单、导航读 [前端界面](standards/frontend-ui.md)；加组件或升级 Spartan 读 [Spartan 维护](standards/frontend-spartan.md)；新增文案读 [前端多语言](standards/frontend-i18n.md) |
<!--#else-->
| 前端功能或修复 | [通用约定](standards/coding-common.md)、[前端](standards/coding-frontend.md)、[测试](standards/testing.md) | 改界面、表单、导航、文案读 [前端界面](standards/frontend-ui.md)；加组件或升级 Spartan 读 [Spartan 维护](standards/frontend-spartan.md) |
<!--#endif-->
| 全栈功能 | 以上两行必读的并集与 [API](standards/api.md) | 同上两行 |
<!--#endif-->
| 只审查代码 | 被审查改动涉及的端对应的必读文档 | 改动触及的专题 |
| 部署、迁移、回滚或恢复管理员 | [部署](deploy/README.md) | [技术栈](standards/tech-stack.md) |

## 全部规范

[通用约定](standards/coding-common.md)、[后端](standards/coding-backend.md)、[API](standards/api.md)、[认证与授权](standards/auth.md)、[服务间调用](standards/service-invocation.md)、[测试](standards/testing.md)、[技术栈](standards/tech-stack.md)、[项目目录](standards/project-structure.md)、[部署](deploy/README.md)。
<!--#if (SpaFrontend)-->
前端：[编码](standards/coding-frontend.md)、[界面](standards/frontend-ui.md)、[Spartan 维护](standards/frontend-spartan.md)。
<!--#if (IncludeLocalization)-->
前端多语言：[frontend-i18n](standards/frontend-i18n.md)。
<!--#endif-->
<!--#endif-->

完整回归入口：`scripts/verify.ps1`。

业务开发和环境交付由项目 Skill [leistd-project-workflow](../.agents/skills/leistd-project-workflow/SKILL.md) 处理。

需求决策、模块契约等按需文档只在产生已确认、需复用的信息时创建，并登记到本索引；一个事实只维护一处，其他位置链接。
