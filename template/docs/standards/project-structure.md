# 项目目录

## 1. 项目根

```text
{project-root}/
├── .agents/skills/          # 跨工具项目 Skill
├── .leistd/template.json    # 来源标记：生成所用的模板包版本与源提交、生成输入、UserSecretsId 与 SDK，不手工修改
<!--#if (Ci == "github")-->
├── .github/workflows/       # CI 薄壳，调用 scripts/verify.ps1，默认分支通过后发布镜像
<!--#elseif (Ci == "gitlab")-->
├── .gitlab-ci.yml           # CI 薄壳，调用 scripts/verify.ps1，默认分支通过后发布镜像
<!--#endif-->
├── backend/                 # .NET 后端
<!--#if (SpaFrontend)-->
├── frontend/                # Angular 前端
<!--#endif-->
├── deploy/                  # 容器编排配置
├── docs/                    # 长期规范与按需沉淀文档
├── scripts/                 # 完整回归入口 verify.ps1；静态检查脚本（错误码闸门总在，其余按启用的功能生成）；来源标记读取与重建 template-marker.ps1；同步到新模板版本 sync-template.ps1
├── AGENTS.md                # AI 协作入口指针（CLAUDE.md 引用它）
├── CLAUDE.md
├── Dockerfile
└── README.md
```

<!--#if (SpaFrontend)-->
项目根包含 `backend/`、`frontend/` 和 `docs/`；monorepo 中所有项目相对路径仍以该层为准。
<!--#else-->
项目根包含 `backend/` 和 `docs/`；monorepo 中所有项目相对路径仍以该层为准。
<!--#endif-->

## 2. 后端分层

```text
backend/src/
├── CompanyName.ProjectName.Domain/
├── CompanyName.ProjectName.Application/
├── CompanyName.ProjectName.Infrastructure/
├── CompanyName.ProjectName.Client/
├── CompanyName.ProjectName.DbMigrator/
└── CompanyName.ProjectName.Api/
```

各层职责、依赖方向与 Domain、Application、Infrastructure 的目录见 [后端开发规范 §2](./coding-backend.md#2-分层与目录)；Api、Client 与 DbMigrator 的目录见下两节，命名空间跟随目录。

测试项目按实际测试类型放在 `backend/tests/` 或解决方案现有位置，不为目录完整性创建空项目。

### 2.1 Api 目录

Api 文件按关注点归入少数顶层目录：

| 目录 | 内容 |
| --- | --- |
| `Controllers/` | 业务 Controller 与 `BaseController` |
| `Auth/` | 认证与授权集成，按下表分类 |
| `Hosting/` | 宿主组装扩展（`*Extensions`）、组件端点映射、`ExceptionMappings/` |
| `Localization/` | 本地化资源标记类型（`ApiResource`） |
| `Notifications/` | 只放依赖宿主资源的通知扩展点实现 |
| `Configuration/` | 宿主级设置绑定 |
| `Options/` | 强类型 Options |
| `Middlewares/` | 中间件 |
| `HealthChecks/` | `*HealthCheck` |
| `HostedServices/` 下的 `Initializer/` | 一次性启动引导 `*Initializer` |
| `Filters/` | MVC/Hub 管道过滤器（按需创建）；名字以 `Filter` 结尾的业务策略按所属功能域放 |

`Auth/` 根目录保留总体注册入口 `AuthenticationExtensions`；其余按职责归位：

| 子目录 | 内容 |
| --- | --- |
| `Authentication/` | 本地会话、远端令牌、外部登录的认证方案注册与签名密钥刷新 |
| `Authorization/` | 授权策略、授权结果处理器与访问控制元数据 |
| `Sessions/` | 会话签发与续期；通用票据存储使用 Security 组件 |
| `OpenIddict/` | 签发服务注册、证书加载、交互保护与协议处理器 |

- 周期任务与常驻消费者不放 Api，目录见 [后台任务规范](./background-jobs.md)。
- 不用笼统的 `Extensions` 命名空间。

### 2.2 Client 与 DbMigrator 目录

两类项目按实际职责分类，不照搬应用层目录。

| 项目 | 目录 | 内容 |
| --- | --- | --- |
| Client | 根目录 | 注册入口 `DependencyInjection`；小型 SDK 的公开客户端接口与 `*Defaults` |
| Client | `Options/` | 具体客户端的配置类型 |
| Client | `Dtos/` | SDK 自有的请求与响应契约；不引用服务内部程序集 |
| DbMigrator | 根目录 | `Program` 与注册入口 `DependencyInjection` |
| DbMigrator | `Runners/` | 命令执行类 `*Runner`，包括迁移与部署引导 |

SDK 扩展为多个业务模块时，客户端接口与 DTO 按模块归位；通用调用管道仍复用框架组件。执行器的内部结果模型可与执行器共置，不为它们另建 DTO 层。

<!--#if (SpaFrontend)-->
## 3. 前端分层

前端目录树、目录职责与依赖方向见 [前端开发规范 §2](./coding-frontend.md#2-目录与依赖方向)。

<!--#endif-->

## 4. 文档与 Skill

`docs/README.md` 是唯一文档索引。`docs/standards/` 是模板提供的规范基线，随模板版本整体更新（`scripts/sync-template.ps1` 按来源标记三方合并模板的新版本）；项目自己的规则写入 `docs/project/`，冲突时以项目规则为准（见 [文档索引](../README.md#规范归属)）。`docs/project/`、`docs/modules/`、`docs/requirements/`、`docs/specs/`、`docs/operations/` 下除 `runbooks/` 以外的子目录和额外部署文档按需创建。

业务开发与环境交付由 `.agents/skills/leistd-project-workflow/` 按场景加载对应 reference，不携带固定文档模板。根目录 `AGENTS.md`（`CLAUDE.md` 引用它）只指向该 Skill 与 `docs/README.md`，并写明规格文档位置，不承载其他规则。

目录和普通 Markdown 文件使用小写 kebab-case（前端多语言 scope 目录名跟随 scope 名）；目录入口统一命名为 `README.md`。代码命名遵循对应语言规范。调整现有目录时同步检查项目引用、导入、构建、部署、测试和文档链接。
