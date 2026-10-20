# Template 场景、功能与端到端验收

状态：已完成（基线为 PR #71 合并后的 develop，随 PR #75 收录）

## 基线与范围

最终基线是 `origin/develop` 的 `0c71ea591bbb7ebc1a512c4be6f032191929d116`（PR #71 合并）。首次完整运行基线为 `0a5d113beed8b68b1d6f47c3715d34ded4cfb7b5`（PR #70）；收尾时发现远端前进，另取新快照进行差异审计和增量验证。当前工作分支未切换。

完整运行快照、生成产品和证据位于 `.tmp/develop-e2e/`；最终快照及最新生成/打包证据位于 `.tmp/develop-latest-audit/`。旧基线完整运行以其源码打包，经隔离 NuGet 源消费。运行证据没有被改写成“在新提交上重跑”，而是通过下述代码等价审计复用。

新增 690 个变更文件中：648 个 C# 文件经 Roslyn 比较，Debug/Release 代码 token、编译指令和模板条件指令的位置均相同；11 个 TypeScript 文件经 TypeScript 解析器比较，代码 token 和模板条件位置相同。其余为注释/文档、已删除文档的一条引用白名单移除；release YAML 和旧术语检查脚本只改注释。模板符号、运行配置、依赖、路由、HTML、CSS 均未改变。审计证据见 [基线更新记录](../../.tmp/develop-e2e/evidence/baseline-update.json)。因此既有业务运行结果适用于代码等价的最终基线；新文档及分发内容另行实际生成检查，最新基线的 22 个生成场景及文档/引用检查、框架 Release 编译与打包均已通过。

模板是通用应用基座，没有订单、CRM、审批等行业业务实体。工作台与平台首页包含骨架/占位内容，不能把它们验收为已实现的行业业务功能。

2026-10-08 补充范围：原五类场景没有充分展开 ServiceClient 多系统业务调用、共享/独立/命名库布局及停用传播策略。详见[多租户与 ServiceClient 集成清单及验收](2026-10-08-serviceclient-multitenant-e2e.md)。本报告的“核心模块通过”不能解释为补充集成矩阵已全部通过；该矩阵以补充报告的实际执行结果为准。

补测首轮完成 12 项：9 通过、3 未通过，同属 F01（Refit 包装异常使不可达/认证协议拒绝的 HTTP 错误分类丢失）。后续已查阅 Refit 官方资料，通过 `TransportExceptionFactory` 修复并完成新包复测；共享/独立租户停用的新交换返回 502、下游停机返回 503，有头闭环通过。真实到期与会话收敛保留首轮证据，详见[修复与复测](2026-10-08-serviceclient-multitenant-e2e.md#f01-官方依据修复与复测)。

## 裁剪条件

| 参数/派生条件 | 默认值或表达式 | 影响 |
| --- | --- | --- |
| ServiceRole | Identity | Identity：本地身份与 OIDC 签发；Standalone：本地身份与 Cookie；Resource：远端令牌验证与本系统授权主体 |
| LocalIdentity | Role != Resource | 密码、注册、个人资料、安全、凭据与本地身份持久化 |
| OpenIddictServer | Role == Identity | 开放应用、/connect/*、授权服务器存储 |
| RemoteTokenAuth | Role == Resource | 远端令牌、签发方就绪、用户投影、资源管理员引导 |
| IncludeFrontend / SpaFrontend | true / LocalIdentity 或 IncludeFrontend | 只有 Resource 可生成纯 API；另外两种形态始终带 Angular 登录界面 |
| ResourceBrowserSession | Resource 且 IncludeFrontend | 服务端 OIDC 机密客户端、Cookie 票据；浏览器不持有 OAuth 令牌 |
| IncludeMultiTenancy | true | 租户解析、数据隔离、分库路由；本地身份形态才有租户控制面 |
| IncludeOperationRecords | true | 历史、检索、导出、归档；关闭后关键操作仍输出结构化日志 |
| Impersonation | LocalIdentity 且 MultiTenancy 且 OperationRecords | 模拟租户管理员与退出恢复 |
| IncludeEmail / Email | true / LocalIdentity 且 IncludeEmail | SMTP、邮件设置、测试发信、邮箱验证、通知邮件；Resource 不发信 |
| IncludeNotifications | false | 通知持久化、未读、中心、个人偏好、站内推送 |
| IncludeRealTime | false | 业务资源订阅、角色列表自动刷新与断线补查；与通知独立，共开时共用连接 |
| IncludeExternalLogin / ExternalLogin | false / LocalIdentity 且 IncludeExternalLogin | GitHub/Google 登录与账号连接；Resource 通过 Identity 登录 |
| IncludeLocalization | false | 前端 Transloco、后端 culture、语言选择；关闭后英语文案与依赖裁剪 |
| Ci | github | github/gitlab/none 流水线薄壳；verify.ps1 总是生成 |

关闭能力需要同时验证文件删除、代码块求值、依赖、路由、菜单、Mock、迁移、测试与文档；不能只检查页面是否隐藏。`TenantController` 主要承载模拟登录，基础租户 CRUD 在框架端点，因此控制器不存在不代表租户管理不存在。

## 常用业务场景与项目清单

| 场景 | 项目清单 | 推荐能力 | 目标 |
| --- | --- | --- | --- |
| A 单组织内部管理系统 | 1 × Standalone | 关闭多租户；邮件、历史、通知按需 | 独立本地账号、用户角色权限、设置与安全管理 |
| B 多租户 SaaS | 1 × Identity 或 Standalone | 多租户、操作历史；邮件、通知按需 | 宿主控制面、租户管理员、共享库/独立库隔离与模拟登录 |
| C 统一身份与多业务门户 | 1 × Identity + 2 × Resource（带前端） | 租户按需；Identity 开放应用；业务本地授权 | 一次登录跨门户、各服务权限独立、可靠退出 |
| D API 与后台服务平台 | 1 × Identity + N × Resource（无前端） | Bearer、机器凭据、交换令牌；实时/通知按需 | 机器调用、委托用户调用、受众与租户传递 |
| E 国际化协作平台 | Identity/Standalone + Resource | 本地身份启用邮件、外部登录；通知、实时、多语言 | 多语言、外部账号、消息与安全提醒、实时协作 |

## 功能与页面验收清单

| 模块/页面 | 闭环与拒绝路径 | 主要场景 |
| --- | --- | --- |
| 公共首页、工作台、403、未知路由 | 未登录入口、登录后跳转、越权提示、占位内容识别 | A–E |
| 登录、注册、两步验证引导/挑战 | 成功/错误密码、验证码、注册配置、TOTP、恢复码、强制两步验证、退出 | A/B/C/E |
| 个人资料 | 保存、撤销、邮箱与手机、头像上传/删除、重载持久化、邮箱验证 | A/B/C/E 的本地身份 |
| 安全设置 | 改密、两步验证启停/恢复码、外部账号绑定/解绑、设备撤销 | A/B/C/E 的本地身份 |
| 用户管理 | 创建/查询/编辑/删除、启停、角色分配、重置密码、解锁、重置 2FA；普通用户拒绝 | A–E；Resource 仅授权主体 |
| 角色与权限 | 创建/编辑/删除、权限树授予/撤销、用户生效、重复名称拒绝、实时刷新 | A–E |
| 系统/个人设置 | 分组发现、逐项保存/恢复继承、重载、用户覆盖/租户/宿主边界；邮件密码保密 | A–E，按生成能力 |
| 租户管理/连接/模拟登录 | 创建初始管理员、启停/编辑/删除、连接配置、行级与库级隔离、模拟与恢复 | B/C |
| 开放应用 | 创建/编辑/删除、回调与作用域、客户端类型、秘密重置、OIDC 生效与拒绝 | C/D/E 的 Identity |
| 操作历史 | 查询、筛选、详情、CSV 导出、成功/失败留痕、归档持久化 | 开历史的所有场景 |
| 通知中心/偏好 | 送达、未读计数、单条/全部已读、历史、实时、个人渠道开关与安全提醒 | B/C/D/E |
| 业务实时 | 资源订阅授权、角色变化刷新、断线补查、与通知共用连接 | C/D/E |
| 多语言 | 语言切换、刷新保持、前后端错误语言、词条完整 | E |
| 邮件/外部登录 | 本地 SMTP 收件、邮箱验证码确认；官方处理器 PKCE/回调与账号链接 | E |
| Resource 与服务调用 | Cookie/Bearer 形态、投影不自动提权、本地权限、受众/签发方、交换令牌、租户传递 | C/D |

## 测试策略、标准与证据

1. 登记全集逐一实际生成，检查存在/缺失文件、禁止词、条件、文档链接；这是裁剪验收，不等于全部产品均构建或浏览器验收。
2. 在真实 PostgreSQL 上执行已有集成与协议测试，覆盖事务、授权、迁移、数据隔离、审计/归档等无直接页面入口的行为。
3. 四服务隔离环境（Identity、两个 Resource、Standalone）使用生产前端构建和有头 Chromium；补充页面用户操作、重载/API/数据库结果核对。浏览器交互与 API 设置测试前置条件分别登记。
4. 外部 OAuth 使用本地协议提供方，但走实际官方客户端处理器；不冒称真实 GitHub/Google 账号上线验收。邮件使用本地 Mailpit；不操作现有业务容器或生产数据。
5. 时间边界优先已有假时钟测试；跨进程令牌到期使用已验证快速配置，观察目标状态，有界等待。

验收目标：已启用能力的核心正常/拒绝闭环均有证据，状态持久化，权限与租户不泄漏；浏览器无阻断错误；关闭能力的文件与入口不残留。每项分别记录通过、失败、跳过、未执行。存在阻断失败或未覆盖核心能力时整体不标记“全部验收通过”。

运行时拓扑、JSON 断言、日志和截图留在隔离 `.tmp/`，含秘密的 HAR/凭据不作为公开报告附件。执行日期为 2026-10-07 至 2026-10-08（Asia/Singapore）。

## 执行结果

本轮已完成裁剪分析、五类场景清单及分层功能验收。所有功能模块均有核心闭环证据；未发现经复现确认的产品阻断缺陷。协议场景经夹具纠正、独立对照及定向复测后，25 个不同场景均有通过记录。首轮脚本并非一次全绿，不能将原始运行标为全绿。

| 验证层 | 实际结果 | 证据（相对仓库根目录） |
| --- | --- | --- |
| 框架源码打包 | 原始与最终 develop 的 Release 打包均成功；完整运行的生成项目使用原始基线本地包源 | `.tmp/develop-e2e/.tmp/pack.log`；`.tmp/develop-latest-audit/.tmp/latest-pack.log` |
| 完整裁剪矩阵 | 原始与最终 develop 均实际生成 22 个场景，文件/条件/依赖声明/文档检查通过 | `.tmp/develop-e2e/.tmp/generation-matrix.log`；`.tmp/develop-latest-audit/.tmp/latest-generation-matrix.log` |
| 代表后端产品 | 6 个生成产品还原、构建、测试通过；629 单元 + 1,410 集成 = 2,039，通过且无跳过 | `.tmp/develop-e2e/.tmp/backend-validation/results.json`、`inventory.json`、各场景 TRX |
| 全功能前端 | 98 个文件、781 项测试通过；TypeScript、样式、Prettier 检查通过 | `.tmp/develop-e2e/.tmp/frontend-test.log`、`frontend-lint.log` |
| 四个前端产品 | Identity/Orders/Billing/Standalone 生产构建并实际运行 | 有头拓扑 `83634-20261007233644965` 下 `frontend-*` 与各服务日志 |
| PostgreSQL 验收 | 真数据库迁移、最小权限、共享租户及独立库隔离通过 | `.tmp/develop-e2e/.tmp/postgresql-run.log` |
| 独立纯 API 验收 | 10 个协议场景通过，含 pure-api-realtime、S10；清理无错误 | `.tmp/develop-e2e/.tmp/pure-api-run.log`；拓扑 `15706-20261008002636712/results.json`、`cleanup.json` |

6 个后端代表产品分别是 `identity-all-features`、`standalone-capabilities-02`、`standalone-capabilities-07`、`resource-capabilities-04`、`resource-capabilities-09`、`identity-tenant-management-without-history`。不能把 22 个生成场景写成 22 个产品全部构建与浏览器通过。

### 实际运行的项目

有头环境使用 Identity、Orders Resource、Billing Resource、Standalone 四个生成产品；开启 Identity 邮件/通知/实时/外部登录/多语言，两个 Resource 用各自 Cookie/OIDC 配置与本地权限，接入真实 PostgreSQL。另起无前端 Billing 的独立四服务环境执行纯 API/机器/交换令牌场景，并按原脚本正常清理。

所有运行端口动态分配，未操作已有业务容器。Identity 及 Resource 前端均为生产构建，浏览器使用 HTTPS；令牌到期用 90 秒快速档实际等待。独立纯 API 复测没有重复等待真实到期，第一次有头环境已执行该边界，不能将纯 API 复测的两个跳过项计为通过。

### 协议、隔离与实时闭环

- S1：浏览器跳转 Identity、密码登录、返回 Resource、受保护页面/API、服务端票据与数据库投影；浏览器存储不含 OAuth 令牌。
- S2–S6/S8：PKCE 与刷新、机器/委托调用、授权/受众拒绝、交换令牌边界、非法头不能替换用户或租户，真实数据库记录核对。
- S7 与 exchange-expiry：用户撤销、既有令牌到期后拒绝、交换 JWT 到期后拒绝；使用真实等待。
- S9/S11：Resource 自动续期、刷新被撤销后重新登录、用户停用后访问收敛。
- S10：Standalone Cookie 正向、外来 Bearer 拒绝、不暴露 OIDC 服务器端点；无前端对照环境原始脚本通过。
- S12：退出确认、继续原始退出请求、跨服务退出。
- MT0–MT5：多租户前置数据、本地权限与行/库隔离、委托租户传递、两个 Resource 单点登录、单点退出、租户停用后到期收敛。
- 实时角色列表：实际 Subscribe、角色创建后自动刷新、通知与业务共用连接、断线期间变化、重连 Resync 补查。纯 API SignalR 另验证合法订阅与非法资源/租户订阅拒绝。
- Google/GitHub：实际官方 OAuth 处理器与本地协议提供方，PKCE、后端换令牌、回调恢复原始授权请求、账号落库。

协议证据位于 `.tmp/develop-e2e/.tmp/oidc-e2e/83634-20261007233644965/`：`assertions.jsonl`、`replay-results.json`/`replay-assertions.jsonl`、`targeted-results.json`/`targeted-assertions.jsonl`、`realtime-final-results.json`；截图分别在 `browser/`、`replay-browser/`、`targeted-browser/`、`realtime-final-browser/`。同一文件可能含首轮失败和后来复测通过，必须结合场景、标签及顺序查看。

### 首轮失败与复测解释

首轮协议运行和页面自动化并非一次全绿，原始失败均保留。

1. 全功能本地化默认中文，已有浏览器脚本硬编码英文按钮。测试前置改为英语后复测；多租户新会话也设置同样前置。
2. 浏览器构建夹具直接运行 `ng build`，漏掉生成项目生产构建要求的 Transloco `postbuild` 优化，出现翻译键。补齐夹具优化并同步静态词条后恢复，产品源代码未修改。
3. 带前端 Standalone 的未知 GET 路径可能被 SPA 回退成 HTML 200，与原 S10 无前端夹具的 404/405 断言不同。独立无前端原始协议运行已通过 S10；HTML 回退不视为 OIDC 令牌签发。
4. 重实时夹具的观测初始化脚本/环境传递不完整；补齐后验证真实订阅、事件与重连通过。
5. 页面等待曾漏掉 Angular 的预加载遮罩、通知遮挡和菜单呈现；一些选择器/DTO 字段/按钮文案断言错误，后续使用当前可访问名称、状态等待与实际 API 字段补测。
6. 同一 TOTP 时间片的验证码不可重复使用。补测按新的时间片生成验证码；这项拒绝行为有后端测试覆盖。
7. 删除采用软删除，用户名/邮箱仍可能保留唯一约束。测试改用独立名称；未把重复名称拒绝误报为产品缺陷。

这些调整只发生在隔离快照的测试夹具、构建前置与浏览器操作脚本，不包含 develop 产品修复。


### 有头页面验收结果

下列操作使用可见 Chrome/Chromium，针对实际生成产品及真实后端执行。通过依据包括页面动作、重新登录/刷新、API 持久化状态及操作历史，不能仅以按钮点击成功作为通过。原始页面尝试在 `83634-20261007233644965/ui-evidence/results.json` 中保留；长流程可能在已成功步骤之后遇到夹具错误，补测记录采用新的用例名称，没有覆盖或删除原始失败。

| 功能模块 | 本轮有头闭环 | 补充验证 |
| --- | --- | --- |
| 公共页/工作台/平台首页 | 首页、工作台、示例占位、平台首页、普通用户 403；亮/暗主题及刷新 | 占位页面没有行业实体；路由/守卫前端测试 |
| 登录/注册 | 实际验证码图片填写、注册、登录；密码登录、外部登录、退出 | 错误密码锁定；默认成员不能读取管理列表；协议跳转与返回 |
| 资料/头像/邮箱 | 保存/撤销、重载；头像上传/重载/删除；邮件验证码实际收件并确认 | API 确认资料、头像与邮箱验证状态；对应审计记录 |
| 个人安全 | 改密、新密码登录、TOTP 启用/登录、恢复码再生成/登录、关闭 | 一次连续 `password-twofactor-complete-final` 通过；重放和错误码拒绝集成测试 |
| 强制二步验证/管理员重置 | 设置策略→受限引导→完成设置→关闭入口隐藏；恢复默认策略、管理员重置、旧会话拒绝、密码重新登录 | `required-twofactor-reset-final-persistence`；引导之前的账号只有受限权限 |
| 登录设备 | 第二有头会话登录、撤销其他设备、该会话 API 401/登录页 | `device-revocation-final`；不会只检查设备列表减少 |
| 外部账号 | 给协议夹具账号设置密码后，解绑 Google、实际本地授权页确认、回调重新绑定 | `external-account-unlink-relink`；Google/GitHub 两种官方处理器另有协议验收 |
| 用户管理 | 新建、编辑、重载、角色分配、禁用/启用、密码重置并登录、锁定后管理员解锁并登录、删除 | 具体审计动作包括 `user.*`、`auth.locked-out`、`auth.login.failed`；超级管理员保护和权限边界集成测试 |
| 角色/权限 | 新建、编辑、授予用户列表权限、用户登录后可读而写入/角色接口 403、入口隐藏；解除授予角色关联后删除 | 有成员角色返回 `RoleStillAssigned` 409；内置角色删除按钮禁用；`assigned-role-rejection-unassign-delete` 通过 |
| 设置/多语言 | SMTP 覆盖、用户通知开关重载保持、恢复继承；语言切换与刷新；强制二步策略逐项保存/恢复 | 用户/租户/宿主覆盖与秘密字段、日期/时区格式等边界由真实 PostgreSQL 集成与前端测试补充 |
| 邮件 | 页面发送测试邮件→Mailpit 收件；资料邮件验证码→页面确认成功 | 邮箱确认 API 状态为 true；SMTP 非 Mock |
| 租户/模拟登录 | 创建管理员、编辑、重载、详情、模拟租户管理员、宿主列表 403、退出恢复宿主、启停、删除 | 共享/独立库隔离及多 Resource 租户身份协议另有 MT0–MT5 验收 |
| 租户连接 | 页面创建使用已迁移独立库的租户，停用后编辑连接版本递增；新增/删除附加连接 | `tenant-dedicated-connection-create-edit-delete`；读取不返回连接串，编辑不预填原秘密；真实隔离由 PostgreSQL/MT 验收 |
| 开放应用 | 机密 Service 应用创建、编辑、密钥重置、删除并核对持久化 | `open-app-create-edit-secret-delete`；浏览器/机器客户端、回调、作用域和拒绝路径由协议与集成补充 |
| 操作历史 | 操作列表、详情入口、页面按钮下载 CSV，检查实际文件内容 | `audit-ui-download-detail`；最后数据库共 140 条操作，43 类动作；归档/保留期通过真实 PostgreSQL 集成测试，无归档页面 |
| 通知中心/偏好 | 安全动作送达、未读、单条已读/移除、全部已读、确认清空、刷新为空；偏好保存/恢复 | `notification-clear-delete-persistence-final`；类型/渠道授权、保留期等通过集成测试 |
| 实时角色列表 | 推送创建后刷新、断线期间变化、重连补查、与通知共用一条连接 | `realtime-final-results.json` 通过；无前端 SignalR 订阅授权另起环境验证 |
| Standalone/Resource | Standalone 登录/管理页面且无开放应用入口；两个 Resource 单点登录、各自权限和退出 | 纯 API Bearer/机器/委托/交换、异常签发方、受众、伪造头拒绝均有独立协议证据 |

最终有头会话的 `errors` 列表为空（`ui-evidence/final-errors.json`）。这表示未捕获到未处理页面异常，不等于每个浏览器网络请求均为 2xx；权限拒绝、锁定、失效与到期测试预期会出现 401/403/409。

### 五类场景的目标与验收判断

| 场景 | 验收目标 | 判断与范围 |
| --- | --- | --- |
| A 内部管理系统 | 本地身份、管理 CRUD、权限拒绝、设置、安全、Standalone 不充当 OIDC 服务器 | 核心闭环通过；最简裁剪产品另有生成及后端构建/测试，未声称它的每个页面都重复跑过 |
| B SaaS | 管理租户、初始管理员、共享/独立库隔离、连接元数据与模拟/恢复 | 通过：UI、真实 PostgreSQL、MT 隔离与租户生命周期相互核对 |
| C 多门户 | 单点登录两 Resource、本地权限不提权、退出与停用收敛 | 通过：两 Resource 的有头协议、数据库投影和 MT3–MT5 |
| D API/后台服务 | 无 SPA、Bearer/机器/委托/交换正确，拒绝非法订阅/头/受众 | 通过：无前端独立运行的 10 个协议场景；到期边界在第一运行中实际等待 |
| E 国际化协作 | 邮件/外部账号/通知/实时/语言/安全完整工作 | 核心闭环通过；OAuth 使用本地协议提供方，未执行真实第三方账号的生产接入验收 |

### 结论与边界

本轮覆盖模板全部功能模块，按上述五类场景完成核心正常与拒绝闭环。结果结合基线等价审计与最新生成检查，支持最终基线上的模板功能验收；未声称穷举所有输入、每个列表筛选排列或 22 种裁剪组合的全部页面。矩阵生成、6 个后端代表产品、4 个有头前端产品与额外纯 API 拓扑的覆盖范围分别登记，互不替代。

多系统集成还需独立核验业务写读删、机器/用户认证方式、本地授权、连接失配、故障恢复和租户停用窗口；SSO、单跳身份探针及已有 MT0–MT5 不能独自完成这一验收。新增 I01–I12 清单与结果在补充报告登记。

非页面能力（归档、保留期、事务、迁移等）使用真实 PostgreSQL 集成/跨进程协议验收。其他输入与配置边界由 2,039 项后端测试和 781 项前端测试补充，不能标成逐项浏览器验收。没有修改 develop 产品实现，也没有替行业业务补写尚不存在的订单/CRM/审批功能。

原有有头协议主脚本清理后仍以非零退出，因为它保留首轮夹具失败；对应成功复测分别有独立结果文件。汇总索引选择有来源的通过证据，没有改写失败。无前端原始协议脚本正常退出，10 个场景通过。

### 证据入口与清理

- [执行索引](../../.tmp/develop-e2e/evidence/execution-index.json)：测试总数、原始 UI 尝试和截图清单。
- [协议最终索引](../../.tmp/develop-e2e/evidence/protocol-final-index.json)：25 个不同场景的通过来源；原始失败仍保存在各运行目录。
- [账号安全截图](../../.tmp/develop-e2e/evidence/account-security.png)、[租户模拟截图](../../.tmp/develop-e2e/evidence/tenant-impersonation.png)、[操作历史截图](../../.tmp/develop-e2e/evidence/operation-records.png)、[实时角色截图](../../.tmp/develop-e2e/evidence/realtime-roles.png)、[工作台主题截图](../../.tmp/develop-e2e/evidence/workspace-light.png)。

两个协议环境的 `cleanup.json` 均为 `processesStopped: true`、`errors: []`。本轮 PostgreSQL 容器/卷和独立 Mailpit 已删除，有头测试会话已关闭；原有 `crm-r17-e2e-*` 容器保持运行。临时明文凭据文件和测试 PFX 已移除，HAR 等敏感原始证据限制权限；公开证据入口不包含凭据、TOTP 密钥或客户端秘密截图。

`.tmp/` 证据为当前工作区附件，不进入版本控制；它们在本轮清理后仍保留，可直接打开。仓库交付为本报告。
