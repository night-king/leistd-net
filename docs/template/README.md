# 模板开发文档

本目录保存 Fullstack App Template 的仓库内部维护规范，不会随 `dotnet new` 生成到业务项目。

| 目标 | 文档 |
| --- | --- |
| 修改模板源码、条件参数、生成项目 Skill 或规范 | [模板开发规范](./development-guide.md) |
| 在常驻生成项目里同步并验证模板改动 | [模板开发工作区](./development-guide.md#5-模板开发工作区) |
| 测试分层、模板矩阵、lint 缓存与验证边界 | [模板质量验证](./quality-assurance.md) |
| 模板包的发布时机、版本推算与变更说明 | [模板版本与发布](./versioning.md) |

生成项目自己的工程规范位于 [`template/docs/`](../../template/docs/README.md)。维护模板时使用 [`developing-leistd-template`](../../.agents/skills/developing-leistd-template/SKILL.md)，仓库维护与生成项目的业务协作分别使用各自的 Skill 和事实源。

[浏览器认证](browser-authentication.md)：官方协议处理器、服务端票据、浏览器会话与部署契约。
