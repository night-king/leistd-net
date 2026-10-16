# 设置内聚、按分组授权与三项新配置实施计划

日期：2026-10-16　分支：`zengqinglei/settings-cohesion`（目标 develop）

## 背景与用户决定

PR #99 把外部登录做成宿主级设置后，留下三项候选配置（通知保留期、会话有效期、自助注册开关）。用户逐项决定：

1. 通知保留期的"未读 ≥ 已读"跨字段约束：**改框架**，清除也经校验器（方案 A），不只写文档。
2. 会话有效期：**按最佳实践终局决策，不留中间态**——直接做租户级空闲超时，不做宿主级过渡版。
3. 自助注册开关：做；**外部登录首次登录不受它限制**，照常自动建号。
4. 配置越加越多，要保障个人设置页、系统设置页的内聚，不让配置散在各处。
5. 设置变多后角色权限要相应调整：系统设置**按面板一条子权限**。

## 依据

- 设计原则 `docs/architecture/design-principles.md` §5：终局不留中间态、优先官方机制、不过度设计、只修默认就会出错的隐患、权衡复杂度。
- 框架规范 `docs/framework/development-guide.md`；组件文档 `framework/docs/components/settings.md`、`authorization.md`、`multi-tenancy.md`。
- 模板规范 `docs/template/development-guide.md`；`template/docs/standards/coding-backend.md` §3.10（设置与 Options）、`auth.md`（权限侧别）、`frontend-ui.md` §2.8（设置页）。
- 用户取舍：终局但不过度设计、不过度考虑安全、权衡复杂度、开发者视角、组件通用性。

## 现状要点（已核对源码）

- 设置写入 `DefaultSettingManager.SetAsync`：`value is null`（清除）时跳过值元数据与全部 `ISettingValueValidator`（`DefaultSettingManager.cs:73`）。
- 宿主级设置整组生效：Options 校验不过时整组退回上一组（`HostSettingsConfigurationProvider.Apply`）。清除绕过跨字段规则后，库里已无覆盖值而生效的仍是旧组，此后任何宿主设置写入都因这一组不合规被整组退回。
- 设置端点 `MapSettings`：`GET /` 走 `AccessPolicy`（模板为"已登录"），返回全部 `IsVisibleToClients` 设置的租户层与宿主层原始值，不按权限过滤；`PUT current-tenant` 只要求一条 `TenantWritePolicy`（模板 `App.Settings`）。用例本身不查权限。
- 模板只有一条 `App.Settings`（Both），覆盖注册、登录安全、SMTP、外部登录、日志、保留期全部分组。前端设置页无可写性判断，只靠路由守卫。
- 权限授予：子权限检查只看自身授予；写入授予时补齐祖先（`EfCorePermissionGrantManager.Normalize`）。管理员角色初始授予只写一次，之后新增权限由管理员显式授予（`SystemInitializer`、`TenantSeeder`），DbMigrator 有 `TenantAdminRestoreRunner` 补缺。
- 会话：`UserSessionOptions.IdleTimeout` 来自 `SessionCookie:ExpireDays`，全进程一份，7 处消费方读 `IOptions`；会话记录只存 `LastSeenTime`，过期按读取时的空闲时长现算。Cookie `ExpireTimeSpan` + 滑动续期，签发处（`SessionCookieIssuer`、模拟登录）不设 `ExpiresUtc`。清理任务按物理库执行，一个库可有多个租户；框架无逐租户迭代 API；`ISettingProvider` 按 scope 记忆化，缓存键不含租户。
- 注册：`POST auth/register`、`POST auth/send-email-code`（只服务注册）无开关；匿名的 `GET auth/security-config` 只在 Email 形态存在。外部登录首次登录在 `ExternalAuthAppService` 自动建号，不读注册策略。
- 主题偏好只在页头切换（本设备 localStorage），个人设置"偏好"面板里没有。

## 目标形态

### 系统设置信息架构

分组按"管理员要完成的事"划分，一个分组 = 一个面板 = 一条子权限；顺序账户 → 集成 → 运维。

| 分组标识 | 面板（zh / en） | 内容 | 子权限 | 侧别 | 条件 |
|---|---|---|---|---|---|
| `Registration` | 注册 / Registration | **开放自助注册**、验证方式与验证码时效 | `App.Settings.Registration` | Both | LocalIdentity |
| `Security` | 登录与会话 / Sign-in & sessions | 登录锁定、两步验证、**会话空闲超时** | `App.Settings.Security` | Both | LocalIdentity |
| `ExternalLogin` | 外部登录 | 不变 | `App.Settings.ExternalLogin` | Host | ExternalLogin |
| `Email` | 邮件发送 | 不变（含发送测试邮件） | `App.Settings.Email` | Host | LocalIdentity && Email |
| `Operations` | 运维 | 日志级别 | `App.Settings.Operations` | Host | — |
| `Retention`（原 `Audit`） | 数据保留 / Data retention | 操作记录归档、**通知清理** | `App.Settings.Retention` | Host | IncludeOperationRecords \|\| IncludeNotifications |

- `App.Settings`（Both）保留为父权限：进入系统设置页与菜单入口。授予任一子权限会自动补齐它。
- 只含宿主级设置的分组侧别为 Host，租户管理员拿不到，按 auth 规范"侧别能表达就只写侧别"。
- `Notifications` 分组只留个人通知偏好，保留期并入 `Retention`（本轮分组布局；`SettingScopes.All` 的设置仍可在两页出现）。

### 个人设置

面板不变：个人资料、账户与安全、通知、偏好。偏好面板加"外观"（亮 / 暗 / 跟随系统），注明只对本设备生效；页头切换保留为快捷入口，两处共用 `ThemeService` 同一份状态。

## 阶段与任务

PR 划分：PR1 = 阶段 1–3；PR2 = 阶段 4；PR3 = 阶段 5；PR4 = 阶段 6。每个 PR 合入 develop 后下一个从新 develop 开分支。

### 阶段 1：框架设置组件（Leistd.Settings.*）

**F1 清除也经校验器。**
- 上下文契约不变（`Value` 仍非空，不新增 `IsClearing`：本次规则只需要候选值，说不出区分写入与清除的消费者）。清除时以"移除本层覆盖后会生效的值"作为候选 `Value` 调用校验器：
  - 用户层：跳过本层，读当前租户的租户层存储值（定义允许租户层时），再无则定义默认值。直接读存储，不用 `GetOrNullAsync`/`GetOrNullForUserAsync`（它们会返回清除前的用户覆盖）。
  - 租户层、宿主层：定义默认值（被绑定的宿主级设置即部署基线）。
  - 回落值一律**明文**：租户层机密覆盖是密文，先解密再交校验器。解析后为 `null` 时不调用校验器；文档注明宿主机密设置的部署配置回落在消费端完成，框架拿不到它。
- 清除不跑值元数据校验（回落值来自定义或下一层）。
- 接口注释、随包文档"写入校验顺序"与"接口参考"改写，按框架规范写破坏性变更说明（清除现在可能被校验器拒绝）。单元测试：清除回落到违规值被拒、已有用户覆盖被排除、定义不允许租户层时忽略其行、租户层机密回落为明文、回落为 null 不调用。

**F2 按分组授权。**（AspNetCore 包，组件只认策略名，不认识权限）
- `SettingEndpointOptions.GroupPolicies`（`IDictionary<string,string>`，分组标识 → 策略名）。
- `PUT current-tenant`：端点仍要求 `TenantWritePolicy`；目标设置所在分组登记了策略时再要求该策略，不满足返回 403（与策略失败同形）。
- 读写同一判据：`CanManage = 定义允许租户或宿主层 && TenantWritePolicy 通过 && (分组未登记策略 || 分组策略通过)`；宿主可达性沿用现有用例（租户上下文不下发进程级设置、写入 `Setting:HostOnly`）。PUT 按同一组合判定。
- `GET /`：`SettingOutputDto` 末尾追加 `bool CanManage`，是 HTTP 管理视图的计算结果，`SettingManagementService` 不引入 HttpContext（端点对用例结果做后处理）。不允许用户层的设置在 `CanManage` 为 false 时不下发；允许用户层的设置照常下发（个人设置页用租户默认值作占位），User-only 项 `CanManage` 恒为 false。这是管理 API 契约变化：`IsVisibleToClients` 是进入客户端 / 管理界面的必要条件，不再代表所有已登录用户都读得到管理值；随包文档里"用例不查权限、GET 不过滤"的说明同步改。
- 每个请求按分组缓存授权结果，不逐设置调用 `IAuthorizationService`。
- `Validate()`：分组标识或策略名为空时注册期抛出。
- 随包文档"注册""实现行为"补说明；端点集成测试覆盖：User-only 项不进系统视图；分组策略通过而 `TenantWritePolicy` 不通过时 `CanManage=false`、管理项不下发、PUT 403；父策略通过而分组策略不通过时同样拒绝；未登记分组回落 `TenantWritePolicy`。

**F3 读取器的记忆化按租户分键。**
- `DefaultSettingProvider` 的租户层、用户层、宿主层缓存按 `CurrentTenant.Id`（用户层再加用户）分键，同一 scope 内切换租户后读到目标租户的快照；`Invalidate` 仍清空本 scope 全部缓存。
- 依赖：`Leistd.Settings.Core` 引用 `Leistd.MultiTenancy.Core`，`ICurrentTenant` 可选注入（参照 `DefaultPermissionChecker`），未注册时按宿主（null）一个键，纯设置消费者不必注册多租户服务。
- 边界：F3 只保证"已有正确上下文时选对快照"。代表哪个租户由 `CurrentTenant.Change` 决定，持久化绑定在哪个租户 / 库由工作单元决定（一个工作单元不能改变连接归属，见 `unit-of-work.md`）；三者各管一层，不互相替代。
- 随包文档"一次请求内每个层级只查一次库"改为"每个租户每个层级只查一次"。单测：先读宿主后 `Change` 到租户、再切回宿主，各自读到本租户值且各查一次库；同租户切用户不复用旧用户覆盖；宿主层不在租户键下复用。

### 阶段 2：模板分组重组与按分组权限

后端：
- `SettingConstant.Groups`：`Audit` → `Retention`；`PermissionConstant.Settings` 加 6 个子常量（随条件裁剪）；`PermissionDefinitionProvider` 按上表侧别登记子权限；中英词条 `Permission:App.Settings.*`。
- `ComponentEndpoints` 配 `GroupPolicies`；`SettingController.SendTestEmailAsync` 改要求 `App.Settings.Email`，`ExternalLoginSettingController` 改要求 `App.Settings.ExternalLogin`；`EmailSettingsAppService`、`SettingChangedAuditHandler` 的授权依据按分组取。
- 词条：`SettingGroup:Retention` 数据保留 / Data retention，`SettingGroup:Security` 登录与会话 / Sign-in & sessions，`SettingGroup:Registration` 注册 / Registration；删 `SettingGroup:Audit`。
- 已有授予：本次是新增子权限、父权限名未变，不触发 authorization 文档"删除 / 改名清旧授予"的迁移规则；新增权限按约定由管理员显式授予（有意的权限收紧，不是兼容层），`TenantAdminRestoreRunner` 可补齐。模板文档说明升级时给已有管理员角色补授子权限；测试验证只有父权限的角色不会自动获得子权限。

前端：
- `setting.dto.ts` 加 `canManage`；`settingsInScope(…, 'system')` 取"允许租户或宿主层 && `canManage`"（层级判断保留作语义表达，正确性以服务端标志为准）。
- `PERMISSIONS.settings` 加子权限常量；系统设置父权限下没有任何可管理分组时显示空态（"没有可管理的设置"），不跳转。
- 邮件测试、外部登录状态部件随分组出现，分组可见即有权限，不另写判断。
- `GROUP_ICONS`、`groupDescriptions`（含不含本地化的 `GROUP_DESCRIPTIONS`）：`Audit` → `Retention`，Security / Registration 说明改写。
- Mock：权限树加子权限；`GET` 按分组过滤、带 `canManage`；`PUT current-tenant`、邮件测试、外部登录状态按分组权限；加一个只有部分子权限的演示角色（如"运维"只有 Operations、Retention）。

规范：
- `coding-backend.md` §3.10、`frontend-ui.md` §2.8 设置页、`auth.md` 权限侧别：写入分组规则（按管理员任务归组、新设置先归入已有分组、同一作用域的设置不散落到多个分组、一个系统设置分组一条子权限、**设置类**快捷入口必须在设置页有对应项且共用同一状态）。`SettingScopes.All` 的设置可以同时出现在系统（租户默认）与个人（用户覆盖）页面，不为此拆定义。

测试：权限目录契约（显示名、译文）自然覆盖；端点授权约定测试；集成测试：只有 Operations 子权限的角色读不到 Email 分组、写 Email 设置 403、写日志级别成功。

### 阶段 3：通知保留期

- 设置（Host，`Retention` 分组，`IncludeNotifications`，不写代码默认值）：`Notifications.RetentionEnabled`（布尔）、`Notifications.ReadRetentionDays`、`Notifications.UnreadRetentionDays`（1–3650）。
- `HostSettingBindings` 三行 `BindOption<NotificationRetentionOptions>`；`DailyRunHourUtc`（登记期读取）与 `BatchSize`（调优）留在部署配置。
- `NotificationRetentionSettingValidator`（`Application/Settings/Validators`）：写入或清除已读 / 未读天数时，候选值与另一项**设置链上的值**（宿主层存储覆盖，无则部署基线，经 `ISettingProvider` 在宿主上下文读取）比较，不读 `IOptionsMonitor`（副本未刷新或上一组被拒时它落后于数据库）。未读 < 已读即抛 `AppSetting:NotificationRetentionOrder`（400），中英文案。整组选项校验继续兜底。
- 集成测试（对照 `OperationRecordArchiveTests`）：写入后 Monitor 立即生效、违序写入被拒、清除导致违序被拒、清除回到部署基线、另一项存储值与 Monitor 快照不同时按存储值判定。
- Mock 定义与面板说明。

### 阶段 4：自助注册开关

- `UserRegistrationOptions.Enabled = true`；设置 `Registration.Enabled`（Tenant，布尔，基线取自配置）放在注册分组首位。
- `UserRegistrationPolicy` 加 `Enabled`；`RegisterAsync` 与 `SendEmailCodeAsync` 开头检查，关闭时抛 `Auth:RegistrationDisabled`，映射 403。外部登录首次登录不读它。
- `GET auth/security-config` 改为所有 LocalIdentity 形态都有：读取方法从 `IEmailVerificationAppService` 移到非邮件专属的认证用例（`IAuthAppService`）；`SecurityConfigOutputDto` 文件按 LocalIdentity 保留（`template.json` 的 Email 排除项移除该文件），`RegistrationEnabled` 无条件，邮件两字段与相关服务依赖留在 `#if (Email)`。
- 前端：摘要类型与读取方法移到 `core/`（首页属 public feature，不依赖 account feature 的 `AccountService`），登录页、注册页、首页读同一份结果，租户选择变化时重新加载。登录页、首页（3 处）注册入口在关闭时隐藏；注册页关闭时显示"未开放注册"空态并给返回登录；Mock 同步。
- 设置说明写明"不影响外部登录首次登录"。
- 测试：关闭时注册与发码 403（含先开页后关闭再提交、已有邮箱验证码也不能绕过）、外部首次登录仍建号、`security-config` 字段；前端单测入口隐藏与空态。生成闭包：Standalone + Email=false、Identity + Email=false 构建与测试，Resource 形态不残留本地注册接口。

### 阶段 5：租户级会话空闲超时

- 设置 `Security.SessionIdleTimeoutDays`（Tenant，1–365，基线取自 `SessionCookie:ExpireDays`），`Security` 分组。
- `ILoginSecurityPolicyProvider`/`LoginSecurityPolicy` 加 `SessionIdleTimeout`（同型扩展，不另立 provider）；领域服务 `UserSessionDomainService` 不依赖应用层，改由调用方传入时长；`UserSessionOptions` 保留为部署基线（组合根按 `SessionCookie:ExpireDays` 配置，只作设置的代码默认值，与 `UserRegistrationOptions` 之于注册策略同型），消费方一律读租户策略。部署基线的装配留在组合根，Application 不反向引用 `Api.Options`；`SessionCookie:ExpireDays` 校验改为 1–365，与设置值域一致。
- **按哪个主体、在哪个租户上下文读策略**（逐点登记）：

  | 消费点 | 主体 / 租户 | 读取边界 |
  |---|---|---|
  | `UserSessionValidator`（Cookie 校验） | 票据主体的租户（认证在 `UseMultiTenancy` 之前，环境尚无租户） | 已在方法内切到主体租户；策略读取放在切换之后，经该租户的读取器 |
  | `AuthPrincipalFactory.IsSessionActiveAsync`（带 sid 的令牌续期） | 令牌所属租户 | 已在新工作单元内切租户；同上 |
  | `SessionIssuer`（新设备判定、`StartAsync` 清本人过期会话） | 待签发主体的租户（登录时即当前租户） | 当前读取器 |
  | 设备列表 `UserSessionAppService`、撤销计数 `RevokeAllAsync` | 当前用户租户 | 当前读取器 |
  | Cookie 签发 / 换发时长（`OnSigningIn`） | 待签发 principal 的租户（模拟开始为目标租户，结束为宿主） | 在 principal 的租户上下文内开 `requiresNew` 工作单元读取 |
  | `OnValidatePrincipal` 跨度收敛 | `context.Principal` 的票据租户（校验器返回时已恢复外层上下文，不依赖它内部 `Change` 的副作用） | 同上，自建租户上下文与 `requiresNew` 工作单元；与签发共用一个局部"按主体读会话时长"方法 |
  | 清理任务 | 库内每个有会话的租户 | 每租户独立读取 |

  同一请求内发生租户转换（模拟开始 / 结束、认证阶段先后读宿主与目标租户）时，`DefaultSettingProvider` 按 scope 记忆化、键不含租户会读到第一份。处理：让读取器的记忆化按租户分键（框架设置组件内的小改动，`_tenantValues` 等按 `CurrentTenant.Id` 缓存），而不是在各调用点新建 DI scope——这是读取器自身的正确性问题，所有切租户的调用方都受益。并入阶段 1 作 F3，补单测"同一读取器先读宿主后切租户读到目标租户值"。
- Cookie：在本地会话方案的官方 `OnSigningIn` 事件里按待签发 principal 的租户策略统一设置 `IssuedUtc = now`、`ExpiresUtc = now + 时长`，覆盖普通登录、模拟开始 / 结束与换发，调用点不各写一遍。`OnValidatePrincipal` 校验通过后若票据跨度（`ExpiresUtc - IssuedUtc`）与当前策略不一致，同时改写 `IssuedUtc = now`、`ExpiresUtc = now + 新时长` 并 `ShouldRenew = true`，交官方处理器续发（续发只改票据时序，不改 `auth_time` 等认证时间 claim）。`ExpireTimeSpan` 仍取部署基线作兜底。
- 清理任务：保留 `ITenantDatabaseRunner` 逐库；每库先在该库的固定上下文内用一个工作单元查出会话表里出现的 `TenantId`（宿主库项的 `TenantIds` 为空，共享宿主库里的租户只能从数据取）并结束它；再对每个 `TenantId`（含宿主 null）在其租户上下文内开独立 `requiresNew` 工作单元读策略、按 `TenantId` 删除。不在已绑定某租户的工作单元里逐个 `Change`。批量化留到有性能证据时。
- 验证器的 1 分钟缓存保持不变（策略最短一天，远大于缓存窗口）。
- 实施取舍（PR 3 审查认可）：跨度收敛只在校验器实际查会话时做——`IUserSessionValidator` 返回 `UserSessionValidation(IsValid, IdleTimeout?)`，由它带出按主体租户读到的时长，缓存命中期间不另读策略，收敛最迟一个缓存周期；签发时由 `ISessionIdleTimeoutReader` 按待签发主体的租户读。租户可用性在读取前检查，"检查之后、连接解析之前租户恰好被删除"的窄窗口不另做异常兜底。
- 失效租户恢复不能被抢先：`UserSessionValidator` 对不存在 / 停用的租户特意返回 true，交给 `UseTenantSessionRecovery` 注销并下发 `X-Tenant-Invalid`；认证事件在该中间件之前，其异常不会被恢复。跨度收敛因此只对可用租户执行——租户不存在或停用时跳过策略读取与跨度改写；读取期间出现明确的租户失效异常（`TenantNotFoundException` 等）同样跳过，交后续管道恢复。不吞任意数据库异常、不以宿主策略替代、不移动恢复中间件。
- 远程令牌形态不出现此设置，`SessionCookie` 仍为部署配置。
- 测试：调短后在途会话下一请求失效；旧票据已存在数天后调长 / 调短，续发的 Cookie 跨度等于新时长；宿主与目标租户时长不同时模拟开始 / 结束各按目标主体；认证前无环境租户时的 Cookie 校验按主体租户；带 sid 的令牌续期按主体租户拒绝过期会话且不更新 `LastSeenTime`；租户与宿主时长不同时正常 Cookie 首次及后续请求都不被宿主策略覆盖、跨度已等于租户策略时不重复续发（覆盖校验缓存命中与未命中）；真实 EF 存储下同一外层 scope 内宿主与两个共享库租户、宿主与独立库租户分别读策略与清理成功；本地身份 + 多租户下保留有效 Cookie 后删除 / 停用租户（校验缓存命中与未命中），API 仍注销并返回 401 + `X-Tenant-Invalid`、HTML 导航仍注销并重定向（在 `TenancyTests.Deactivated_tenant_rejects_existing_sessions_and_new_sign_ins` 基础上补删除租户场景）。

### 阶段 6：偏好面板外观

- 偏好面板顶部加"外观"行（与设置行同写法的下拉，亮 / 暗 / 跟随系统），复用 `ThemeService.setMode`；说明"只对本设备生效"；页头切换不变。
- 单测：面板切换与页头状态同步。

## 验证

- 每阶段：受影响组件单元 / 集成测试；框架改动打包到 `.tmp/local-feed` 后经 NuGet 消费。
- PR 前：四形态前端（fe-full / ui-noloc / ui-minimal / ui-resource）build、lint、test；后端矩阵 a、b 场景；真实 PostgreSQL 场景跑一次设置与会话集成测试。
- 端到端（有头浏览器，Mock 4311 与真实后端各一轮，按 PR 增量）：按分组权限的面板可见性与 403、数据保留面板违序提示、关闭注册后入口隐藏与直达空态、会话调短后在途会话失效、偏好外观与页头同步。
- 界面改动先出对比页给用户确认（系统设置面板与空态、注册关闭态、偏好外观）。
- 每阶段送 Codex 审查；代码完成后整体再审。

## 不做

- 会话绝对超时（最长登录时长）：新能力，未要求；滑动空闲超时加设备列表自助下线已覆盖。
- 分组之间的联动置灰（注册关闭后其余注册项）：分组说明写明即可。
- 自动迁移已有角色授予：框架约定新增权限显式授予，父权限名不变。
