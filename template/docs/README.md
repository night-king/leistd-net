# MyProject 文档

本目录保存需要长期复用的项目知识；源码、配置、测试、Git 和 CI 已能表达的不重复写成文档。

## 按任务读取

任务跨多类时取并集；表中已链接 `standards/` 的全部规范，未列出的任务按主题选读。

| 任务 | 必读 | 按需 |
| --- | --- | --- |
| 后端功能或修复 | [通用约定](standards/coding-common.md)、[后端](standards/coding-backend.md)、[测试](standards/testing.md) | 改接口、分页排序或输入验证读 [API](standards/api.md)，批量或统计接口另读 [批量与统计](standards/api-batch-statistics.md)；设置项读 [设置](standards/settings.md)，后台或周期任务读 [后台任务](standards/background-jobs.md)；涉及登录、会话、权限或租户读 [认证与授权](standards/auth.md)；调用其他服务读 [服务间调用](standards/service-invocation.md) |
| 调整后端目录 | [后端目录规则 §2](standards/coding-backend.md#2-分层与目录)、[项目目录](standards/project-structure.md) | 按影响面读取测试与相关专题 |
<!--#if (SpaFrontend)-->
| 前端功能或修复 | [通用约定](standards/coding-common.md)、[前端](standards/coding-frontend.md)、[测试](standards/testing.md) | 改界面读 [前端界面](standards/frontend-ui.md)，按内容另读 [列表与表格](standards/frontend-lists.md)、[表单](standards/frontend-forms.md)、[导航](standards/frontend-navigation.md)（新页面、菜单）、[路由守卫](standards/frontend-routing.md)、[时间、图表与推送](standards/frontend-data.md)，设置页读 [设置](standards/settings.md)；加组件或升级 Spartan 读 [Spartan 维护](standards/frontend-spartan.md) |
| 全栈功能 | 以上两行必读的并集与 [API](standards/api.md) | 同上两行 |
<!--#if (IncludeLocalization)-->
| 新增界面文案 | [通用约定](standards/coding-common.md)、[前端](standards/coding-frontend.md)、[前端多语言](standards/frontend-i18n.md)；只改文案由词条闸门验证，不读测试规范 | 校验提示读 [表单](standards/frontend-forms.md) |
<!--#else-->
| 新增界面文案 | [通用约定](standards/coding-common.md)、[前端](standards/coding-frontend.md)、[界面文案](standards/frontend-ui.md#3-界面文案) | 校验提示读 [表单](standards/frontend-forms.md) |
<!--#endif-->
<!--#endif-->
| 只审查代码 | 改动所属任务行的必读文档（全栈改动按全栈功能行） | 改动触及的专题 |
| 部署、迁移或回滚 | [部署](deploy/README.md) | [配置参考](deploy/configuration.md)、[技术栈](standards/tech-stack.md) |
| 证书轮换、数据库升级等运维操作 | [运维手册](operations/runbooks/)中对应的一份 | |
| 线上查询、客户问题排查、数据修正、事件复盘 | 项目 Skill 的运维操作流程 | [部署](deploy/README.md) |

完整回归入口：`scripts/verify.ps1`。同步到新的模板版本：`scripts/sync-template.ps1`，按项目 Skill 的同步模板流程执行。

业务开发、环境交付和运维操作由项目 Skill `leistd-project-workflow` 处理。

## 规范归属

`standards/` 是项目模板提供的 .NET/Angular 工程规范基线，随模板版本整体更新，项目内不改写。项目自己的规则写入 `project/{topic}.md`，与 `standards/` 冲突时以它为准。

业务规则、模块契约、运维手册与可复用查询分别写入 `requirements/`、`modules/`，以及 `operations/` 下的 `runbooks/`、`queries/`；除模板自带的手册外，这些常驻文档首次有内容时创建并登记到本索引，一个事实只维护一处。

`specs/` 与 `operations/` 下的 `data-fixes/`、`incidents/` 是按日期命名的依据文档，记录某次变更或操作的原因与结果；AI 默认不读，需要追溯时再读，不登记到本索引。
