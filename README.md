# leistd-net

> **为 AI 时代设计的 .NET 10 + Angular 22 全栈 DDD 基座**：框架让 AI 按真实 API 写代码，Skills 按场景提供执行知识，模板验证可运行的端到端组合。

[![Release](https://github.com/zengqinglei/leistd-net/actions/workflows/release.yml/badge.svg)](https://github.com/zengqinglei/leistd-net/actions/workflows/release.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

## 为什么是 leistd-net

传统脚手架解决"代码怎么起步"。AI 参与开发之后，瓶颈变成两件事：**AI 会不会凭记忆臆造框架 API**，和**它交付的东西可不可追溯**。leistd-net 把这两件事当一等目标：

- 🤖 **按真实 API 编码**——每个 `Leistd.*` 包内置随版本分发的用法文档，XML 注释提供精确契约与关键示例；配套 Skill 引导 AI 先查已安装版本再写代码，CI 校验文档与源码不漂移。
- 🔄 **按风险完成闭环**——模板携带跨工具项目 Skill，按需加载方案、实现、审查、测试与部署知识；简单改动直接实施，高风险动作由人确认。
- 🧩 **文档按价值沉淀**——只把需要跨会话或长期复用的信息写入权威位置，不生成例行报告与占位模板。

两个交付面清晰分离：

- **`framework/`** —— 按能力分组的 `Leistd.*` 组件 + DDD 四层基础类型：AOP、DI、核心原语、连接解析、事件总线、异常、本地化、分布式锁、多租户、对象映射、统一响应、安全、服务间调用、链路追踪、工作单元、审计、通知、实时、授权（含资源实例授权与数据范围）。完整清单见[组件总览](framework/docs/components/README.md)。统一版本、中央包管理（CPM）、Source Link 源码调试，以 NuGet 发布，**每个包内置随版本文档**。
- **`template/`** —— 基于 `dotnet new` 的全栈项目模板（.NET 10 后端 + Angular 22 前端，DDD 四层），支持 `Identity` / `Standalone` / `Resource` 三种服务形态，可独立选择多租户、通知、业务实时、邮件、操作历史、外部登录与本地化；Resource 还可生成无前端的纯 API。生成的项目通过 NuGet 引用 framework，并自带项目 Skill 与工程规范。

---

## AI 协作开发（核心特性）

仓库维护与生成项目各用自己的 Skill，从所属交付面的源码与文档建立事实。

### 维护本仓库

仓库内部 Skill 位于 [`.agents/skills/`](.agents/skills/)，这是唯一权威源。[Codex](https://developers.openai.com/codex/skills) 等原生发现该目录的 AI CLI 直接使用；[Claude Code](https://code.claude.com/docs/en/skills#where-skills-live) 读取的 `.claude/skills` 是随仓库提交的目录符号链接（`-> ../.agents/skills`），克隆或新建工作树即可用，修改、新增、删除 Skill 都无需同步。

Windows 检出符号链接需要开启开发者模式并设置 `git config --global core.symlinks true`；未开启时 `.claude/skills` 会检出成一个文本文件，可在仓库根执行 `mklink /D .claude\skills ..\.agents\skills` 手动重建。不要用 `npx skills add ./.agents/skills --agent claude-code` 生成适配：源目录就是 `.agents/skills` 本身时它会复制而不是链接，副本不随权威源更新。

#### 引入第三方 Skill

引入外部 Skill（如前端 UI 库 `spartan`）时，拉取到 `.agents/skills/` 作为权威源并随仓库提交，经上面的链接自动对 Claude Code 可见：

```bash
npx skills add spartan-ng/spartan
```

配套的 MCP server 在仓库根 [`.mcp.json`](.mcp.json) 声明（如 `@spartan-ng/mcp`），随仓库提交、开箱可用。

### 生成项目

模板生成的项目使用一个 Skill 路由最终意图，再按需加载场景知识：

| 机制 | 作用 |
| --- | --- |
| **项目 Skill** | `leistd-project-workflow` 覆盖规划、实现、审查、测试、协调和部署，并保持最终交付责任 |
| **项目事实源** | `docs/README.md` 是文档索引；源码、配置、测试和 CI 定义实际行为 |
| **事实优先** | 先读源码、配置、测试、框架随包文档和最新同类项目文档，不从训练记忆或固定模板猜测 |
| **按需沉淀** | Git、PR、CI 和当前答复承载一次性证据；长期共享的规则、需求、模块或运维事实才进入 `docs/` |
| **人工确认红线** | 生产部署 / 删数据 / 改密钥等高风险动作必须人类显式确认 |

> 总体原则见 [`docs/architecture/design-principles.md`](docs/architecture/design-principles.md)，三层使用场景和逐步文档链路见 [`docs/architecture/collaboration-scenarios.md`](docs/architecture/collaboration-scenarios.md)。

---

## 仓库结构

```
leistd-net/
├── .agents/skills/     # 仓库内部维护 Skill
├── framework/          # Leistd 框架（NuGet 化，独立版本）
│   ├── components/     #   共享组件（按能力分组，清单见 framework/docs/components/README.md）
│   ├── ddd-struct/     #   DDD 四层基础类型
│   ├── docs/           #   面向使用者的组件文档（随包分发）
│   └── build/          #   本地打包、包消费验证与文档-源码漂移校验脚本
├── template/           # dotnet new 项目模板
│   ├── backend/        #   .NET 10 + DDD 后端
│   ├── frontend/       #   Angular 22 前端
│   ├── .agents/skills/ #   跨工具项目 Skill
│   └── docs/           #   项目规范及按需沉淀的长期文档
├── scripts/            # 本地开发与 CI 共用的仓库级验证脚本
├── skills/             # 面向 Leistd.* 使用者的可分发 Skill
├── docs/               # 仓库架构、框架/模板内部维护规范与变更记录
├── VERSION             # 版本基准（唯一版本来源，x.y.z）
└── .github/workflows/  # CI 与多通道发布流水线
```

---

## 快速开始

### 给 AI 装上框架知识（框架级 Skill）

让你的编码 Agent（Claude Code / Cursor / Codex 等）学会按 `Leistd.*` 真实 API 编码、不臆造：安装框架级 Skill `leistd-net-framework`。它是**索引 Skill**，指向随每个 NuGet 包分发的版本正确文档，AI 用的始终是你安装的那个版本的真实 API。安装命令与项目级 Skill 的安装方式见 [`skills/README.md`](skills/README.md#安装)；模板生成项目已包含 `leistd-project-workflow`，无需重复安装。

### 用模板创建项目

```bash
# 安装模板（本地）
dotnet new install ./template

# 生成项目（命名空间将替换为 Acme.Shop.*）
dotnet new fullstack-app -n Acme.Shop
```

模板发布为 NuGet 包 `Leistd.Templates`，版本独立于框架（tag `template-vX.Y.Z`，规则见 [版本与发布规范](docs/framework/versioning.md#模板包发布template-通道)），可用 `dotnet new install Leistd.Templates::<版本>` 安装；生成项目在来源标记（模板源 `template/.leistd/template.json`）里记录来源模板版本与生成参数。

生成的后端默认通过 NuGet 引用 `Leistd.*` 框架包，并自带按场景触发的 AI 协作能力。可用参数以 `dotnet new fullstack-app --help` 为准：

| 参数 | 默认值 | 生效范围与能力 |
| --- | --- | --- |
| `--service-role` | `Identity` | `Identity`：本地身份与 OIDC 签发；`Standalone`：本地身份与 Cookie，不带授权服务器；`Resource`：验证远端令牌，保留本地用户投影、角色与权限 |
| `--include-frontend` | `true` | 仅 Resource 可关闭；关闭后只接受 Bearer，移除 Angular、浏览器 OIDC 与 Cookie 会话。Identity/Standalone 始终包含交互登录前端 |
| `--include-multi-tenancy` | `true` | 全部角色；控制租户解析、连接路由、分库及租户选择；本地身份形态还包含租户管理与控制库。关闭后仅接受宿主身份 |
| `--include-real-time` | `false` | 全部角色；业务事件发布与资源订阅，独立于通知 |
| `--include-email` | `true` | 仅 Identity/Standalone；控制 SMTP、邮箱验证、邮件设置及通知邮件渠道。Resource 不发送邮件 |
| `--include-operation-records` | `true` | 全部角色；控制内置数据库历史、查询、导出、归档及界面。关闭后仍输出结构化安全记录 |
| `--include-notifications` | `false` | 全部角色；通知发布、历史、未读数、通知中心、推送及个人偏好，独立于业务实时 |
| `--include-external-login` | `false` | 仅 Identity/Standalone；GitHub/Google 等第三方身份提供商登录 |
| `--include-localization` | `false` | 全部角色；后端按 culture 本地化，有前端时同时包含语言资源与运行时切换 |
| `--ci` | `github` | 全部角色；CI 流水线薄壳：`github` 生成 `.github/workflows/ci.yml`，`gitlab` 生成 `.gitlab-ci.yml`，`none` 不生成。薄壳只准备环境并调用随项目生成的完整回归入口 `template/scripts/verify.ps1`（生成后位于 `scripts/`） |

**常用项目怎么选：** 按场景选一条命令，仅指定需要改变的参数，其余采用表中默认值；多租户默认开启，不需要额外传参。

```bash
# 多租户身份服务：含登录前端、本地用户与 OIDC 签发
dotnet new fullstack-app -n Acme.Identity

# 单租户身份服务
dotnet new fullstack-app -n Acme.Identity --include-multi-tenancy false

# 精简独立应用：含前端与本地登录，不带 OIDC 签发、多租户、邮件和数据库操作历史
dotnet new fullstack-app -n Acme.App --service-role Standalone --include-multi-tenancy false --include-email false --include-operation-records false

# 带前端的多租户业务服务：通过远端 Identity 登录
dotnet new fullstack-app -n Acme.Orders --service-role Resource

# 多租户资源 API：只接受远端 Bearer，不生成前端
dotnet new fullstack-app -n Acme.OrdersApi --service-role Resource --include-frontend false

# 精简单租户资源 API：不带数据库操作历史，保留结构化安全记录
dotnet new fullstack-app -n Acme.InternalApi --service-role Resource --include-frontend false --include-multi-tenancy false --include-operation-records false
```

需要通知中心时追加 `--include-notifications true`；需要业务实时订阅时追加 `--include-real-time true`，两者可独立选择。需要多语言时追加 `--include-localization true`。

角色专用参数在其他角色下不生效。通知与业务实时支持四种组合；都开启时共用一个 Hub，有前端时只建立一条连接。模拟登录由“本地身份 + 多租户 + 操作历史”共同决定，关闭历史仍可管理租户，但不提供模拟登录。

裁剪发生在生成时，覆盖用例、依赖、DI、端点、迁移、前端、Mock、测试和部署资产；它不是已部署应用的运行时开关。所有角色保留授权、实体审计、软删除与必要的安全记录；本地身份的失败计数、账户锁定和会话撤销不作为可选裁剪能力。单租户保留基础 `TenantId=null` 模型与宿主执行能力，不保留多租户产品入口。

操作历史关闭后，成功安全记录在工作单元提交后输出，失败记录立即输出；进程在提交后、输出前退出仍可能丢记录。数据库历史模式只有在业务环境事务及同一存储内记录，才提供与业务数据同事务的保证。具体注册和保证见[操作记录组件](framework/docs/components/operation-records.md)。Framework 提供通用契约与适配器，Template 负责角色组合和产品资产裁剪；六个后端项目名称保持不变。

Resource 的首次管理员授予使用生成项目的 DbMigrator `--grant-admin <sub>`，默认 dry-run，显式 `--apply` 才写入；完整命令见[后端说明](template/backend/README.md#资源管理员首次授予)。参数与有效能力的维护规则见[模板开发规范](docs/template/development-guide.md#3-条件生成)。

### 本地构建框架

```bash
# 构建
dotnet build framework/Leistd.Framework.slnx -c Release

# 本地包固定输出到 .tmp/local-feed；脚本会先清空该目录，避免已删除组件的旧包残留
pwsh framework/build/pack-local-feed.ps1
```

---

## 文档

| 我想…… | 看这里 |
| --- | --- |
| 给 AI 装上框架知识（安装 / 了解框架级 Skill） | [可分发 Skill 说明](skills/README.md) |
| 用框架的某个能力（锁 / 事件 / 响应 / 追踪……） | [框架文档首页](framework/docs/README.md) → [组件总览](framework/docs/components/README.md) |
| 用模板生成 / 配置项目 | [用模板创建项目](#用模板创建项目) |
| 了解三层架构与 AI 协作场景 | [三层交付与 AI 协作](docs/architecture/collaboration-scenarios.md) |
| 在业务项目中使用 AI 协作 | [项目协作 Skill](template/.agents/skills/leistd-project-workflow/SKILL.md) 与 [项目文档入口](template/docs/README.md) |
| 给框架新增或修改组件 | [开发规范](docs/framework/development-guide.md) |
| 修改模板、条件裁剪或生成项目工作流 | [模板开发规范](docs/template/development-guide.md) |
| 了解版本与发布机制 | [版本与发布](docs/framework/versioning.md) |
| 联调本地框架与模板 | [模板联调说明](docs/template/development-guide.md#5-本地框架联调) |

---

## 版本与发布

版本基准存于仓库根 [`VERSION`](VERSION)，由 [`release.yml`](.github/workflows/release.yml) 按 [Conventional Commits](https://www.conventionalcommits.org/) 推算递增并分通道发布；提交格式直接决定版本号。递增规则、版本形态、发布通道与恢复流程见 [版本与发布](docs/framework/versioning.md)。

---

## 技术栈

| 层 | 技术 |
| --- | --- |
| 后端 | .NET 10 · ASP.NET Core · EF Core；Identity/Resource 使用 OpenIddict |
| 前端 | Angular 22 · Spartan UI · Tailwind CSS；纯 Resource API 不包含前端 |
| 数据 | PostgreSQL 15+；Redis 7+ 承载缓存与锁；多副本必需，单实例可回落到进程内缓存与锁（启动告警） |
| 部署 | Docker · Docker Compose |

---

## 许可

[MIT](LICENSE) © zengql
