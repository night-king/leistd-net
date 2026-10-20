# CompanyName.ProjectName

基于 .NET 10 和 Leistd.* 组件构建的应用，后端采用 Domain、Application、Infrastructure、Api 四层结构。
<!--#if (SpaFrontend)-->
前端使用 Angular 22。
<!--#endif-->

## 项目结构

```text
CompanyName.ProjectName/
|-- backend/                 # .NET 后端
<!--#if (SpaFrontend)-->
|-- frontend/                # Angular 前端
<!--#endif-->
|-- deploy/                  # Docker Compose 配置
|-- docs/                    # 项目规范与按需沉淀文档
|-- scripts/                 # 完整回归入口 verify.ps1 与静态检查脚本（Python 3）
|-- .agents/skills/          # 跨工具项目 Skill
<!--#if (Ci == "github")-->
|-- .github/workflows/       # CI 薄壳，调用 scripts/verify.ps1，默认分支通过后发布镜像
<!--#elseif (Ci == "gitlab")-->
|-- .gitlab-ci.yml           # CI 薄壳，调用 scripts/verify.ps1，默认分支通过后发布镜像
<!--#endif-->
`-- Dockerfile
```

## 已启用能力

- EF Core 数据访问、审计字段、软删除、仓储与应用服务基座。
<!--#if (LocalIdentity)-->
- 本地账号、登录、注册和 Cookie 认证。
- 用户、角色、权限以及超级管理员授权模型。
<!--#if (IncludeMultiTenancy)-->
- 多租户控制面、租户管理与登录页租户选择。
<!--#endif-->
<!--#if (OpenIddictServer)-->
- OpenIddict OAuth 2.0/OIDC Server。
<!--#endif-->
<!--#if (ExternalLogin)-->
- GitHub、Google 等外部身份提供方登录。
<!--#endif-->
<!--#endif-->
<!--#if (!LocalIdentity)-->
- 远端 Bearer 验证、用户投影与本服务角色/权限；首位投影用户不会自动获得管理员权限。
<!--#if (ResourceBrowserSession)-->
- 后端 OIDC 机密依赖方与服务端 Cookie 票据。
<!--#endif-->
<!--#endif-->
<!--#if (IncludeNotifications)-->
- 通知持久化、未读状态与站内推送。
<!--#if (LocalIdentity)-->
- 安全提醒（新设备登录、密码与两步验证变更、账号锁定）：站内通知始终送达；用户在个人设置「通知」面板按类别与渠道选择接收方式。
<!--#endif-->
<!--#endif-->

<!--#if (IncludeRealTime)-->
- 业务实时事件与受授权保护的资源订阅；角色新建、修改、删除后角色列表自动刷新，断线恢复后补查一次（用户角色分配引起的人数变化不推送）。
<!--#endif-->
<!--#if (IncludeMultiTenancy)-->
- 租户作用域的数据隔离与独立数据库路由。
<!--#else-->
- 仅宿主作用域：业务数据保留 nullable TenantId 与审计字段，认证入口拒绝租户身份。
<!--#endif-->
<!--#if (IncludeOperationRecords)-->
- 内置操作记录存储、查询、导出及归档；成功记录随业务事务持久化。
<!--#else-->
- 必需的结构化安全记录；成功在工作单元提交后输出，失败立即输出。提交与日志输出之间进程退出可能丢记录。
<!--#endif-->

## 本地运行

### 后端

```bash
docker compose -f deploy/docker-compose.dev.yml up -d
cd backend
dotnet run --project src/CompanyName.ProjectName.DbMigrator -- --apply   # 首次启动前、拉到新迁移后
dotnet run --project src/CompanyName.ProjectName.Api
```

<!--#if (RemoteTokenAuth)-->
启动前先按 [后端说明](backend/README.md#对接-identity) 配置签发方并在 Identity 完成登记。
<!--#endif-->
配置分层与 user-secrets、迁移预演、开发依赖与健康检查见 [后端说明](backend/README.md)。

<!--#if (SpaFrontend)-->
### 前端

```bash
cd frontend
npm ci
npm start
```

浏览器打开 `http://localhost:4200`，开发代理与 Mock 见 [前端说明](frontend/README.md)。

<!--#endif-->
## 验证

完整回归（随包静态检查、构建、全部测试）只有一个入口，本地与 CI 共用；需要 Docker（集成测试），步骤清单用 `-List` 查看：

```bash
pwsh scripts/verify.ps1
```

运行前提、何时跑全量以及编辑中按改动收窄的命令见 [测试规范](docs/standards/testing.md#11-完整验证由谁承担)。

## AI 协作

项目级 [协作 Skill](.agents/skills/leistd-project-workflow/SKILL.md) 按任务加载开发、验证或环境交付 reference。[项目文档入口](docs/README.md)、源码、配置和测试提供工程事实。

[Codex](https://developers.openai.com/codex/skills) 等原生发现 `.agents/skills/` 的 AI CLI 直接使用。[Claude Code](https://code.claude.com/docs/en/skills#where-skills-live) 读取 `.claude/skills/`：在项目根建一次目录链接并提交到项目仓库，此后克隆、新建工作树即可用，修改或新增 Skill 无需同步：

```bash
mkdir -p .claude && ln -s ../.agents/skills .claude/skills
```

Windows 需开启开发者模式并设置 `git config --global core.symlinks true`，在项目根执行 `mklink /D .claude\skills ..\.agents\skills`；未开启时检出的 `.claude/skills` 是一个文本文件，按此命令重建即可。不要用 `npx skills add ./.agents/skills --agent claude-code` 生成适配：源目录就是 `.agents/skills` 时它复制而不链接，副本不随项目 Skill 更新。AI CLI 完全不支持 Skill 时，在当前会话中明确要求它先读取 `.agents/skills/leistd-project-workflow/SKILL.md`。

使用 `Leistd.*` 组件时，按 [后端说明](backend/README.md#leistd-框架-api) 定位当前 NuGet 包版本的文档和 XML，不根据模型记忆猜测 API。

## 部署

```bash
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.override.yml up --build
```

生产部署前应确认数据库迁移、密钥、管理员密码、HTTPS、健康检查和回滚方案；变量清单、迁移预演与已部署版本的确认方式见[部署说明](docs/deploy/README.md)。
