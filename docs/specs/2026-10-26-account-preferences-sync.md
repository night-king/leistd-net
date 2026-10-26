# 账户偏好跨服务同步（下游复议 F2、F5）

状态：实施中（第四稿方案审查通过） · 风险档：高（跨服务认证与授权、模板 Identity 与资源服务接线、前端偏好来源切换） · 关联：`2026-10-26-account-preferences-feedback.md` §4「复议」

## 1. 需求

### 问题与背景

模板能生成「签发方（Identity）+ 带前端的资源服务」组合。现在每个服务各存一份 `Display.Language`、`Display.TimeZone`、`Display.Theme`：

- 用户在一站改了语言，另一站不跟随，要每站各设一遍；
- 服务端给收件人产出邮件、通知时，各服务按各自的值解析。

`template/docs/standards/settings.md` §4 写了「资源服务不拥有账户偏好」，但模板代码让资源服务各存一份，规范与代码不一致。

### 用户裁决（2026-10-26）

- **F2、F5 采纳，只改模板**：Identity 持有并计算账户偏好，经 `/me` 下发；资源服务读取 Identity 并缓存；框架不改。
- **第二稿撤回**：框架「远端设置」方案（第二稿）撤回。它两轮审查都没通过，一个通用机制只服务这一个场景。
- **F4 组织启用层撤回为项目级**：它属于业务策略。部署级的语言限制用能力清单已能做到。

### 业界参照

账户服务持有偏好，应用读取：

- OIDC `profile` 范围下有标准声明 `locale`、`zoneinfo`。Google 的 ID 令牌带 `locale`；Keycloak 把 `locale` 存为用户属性再映射成令牌声明。
- Microsoft 的用户属性有 `preferredLanguage`，Graph 提供 `/me/settings/regionalAndLanguageSettings`。
- abroker 三仓：`/me` 带偏好，资源服务经 Identity 读取。

### 范围

- **存储**：仍是 Identity 与 Standalone 的设置（组织默认 + 个人覆盖），不改框架设置组件。
- **新增**：
  - Identity 的偏好读写接口；
  - 资源服务一侧的读取客户端与缓存（模板代码，各资源服务随模板同步）；
  - 前端用 `GET /me` 初始化与重查偏好，用 `PATCH` 的成功响应更新同一份偏好状态；
  - OIDC 声明。
- **不在范围**：组织启用层、偏好变更事件推送、资源服务旧本地偏好的自动迁移。

### 验收标准（Given / When / Then）

**统一来源**

- P1：Given 任一形态（Standalone、Identity、Resource）已登录。When 请求 `GET /api/v1/auth/me`。Then 响应带 `preferences` 与 `preferenceOverrides` 两个对象，二者同时存在或同时为 null：
  - 都存在时为「已知」：
    - `preferences` 是 `language`、`timeZone`、`theme` 的生效值；
    - `preferenceOverrides` 是个人覆盖原值；
    - 对象内字段为 null 表示已知未设置：语言与时区由前端按设备回落（环境回落，不是 Resource 新造的账户默认值）。
  - 都为 null 表示「暂时读不到」（只有 Resource 在没有旧表示时遇到上游故障才会出现）。
  前端用它初始化与重查偏好（写入结果另由 `PATCH` 响应更新，见 P4）。
- P2：Given Resource 已缓存该用户语言 en 且仍在新鲜期内，用户随即在 Identity 把语言改为 zh-CN。When 切回 Resource 页签（F3 重查 `/me`）。Then 资源服务界面为 zh-CN。
  - 当前用户的 `/me` 每次都向 Identity 做条件重验证，不受新鲜期限制，也不合并任何在它之前已经发出的回源，见 P11、P13。
  - 本人在 Resource 实例 A 写入后，`/me` 落到另一个持有旧缓存的实例 B，同样得到新值。
  - 交错用例：B 上一次服务端读取在写入之前已取得旧表示，响应延迟到新 `/me` 发起之后。新 `/me` 不得消费这次旧回源。
  - 资源服务本地不存这一项。
- P3：Given 两端都没有组织与个人覆盖，Identity 部署基线为 zh-CN / Asia/Shanghai，资源服务部署基线不同。Then 资源服务的生效值为 zh-CN / Asia/Shanghai。资源服务不再有 `Display.*` 设置定义与部署基线。

**写入**

- P4：用户在任一形态的偏好面板或页头切换语言、外观、时区。Then 写入 `PATCH /api/v1/auth/me/preferences`（合并补丁：缺省不变，`null` 清除）。
  - 成功响应直接返回写入后的完整表示（`preferences` 与 `preferenceOverrides`），由 Identity 在提交后计算。前端用它应用，不另行回读。清除覆盖的情形因此也能得到权威的回落值（例如回落到组织默认），前端不必推导。
  - Identity / Standalone 本地经 `ISettingManager` 写入；
  - Resource 以 Token Exchange 代表用户本人转交 Identity 的同名接口，成功后作废本服务对该用户的缓存，随后读到新值。
- P5：Resource 转交时，Identity 拒绝写入（非法语言或时区等）。Then Resource 返回同一错误码、同一状态与本地化说明，不是 502。
- P6：Resource 转交失败的归类。现有跨服务信号无法可靠区分「用户凭证失效」与「交换或目标配置错误」：模板 `ConnectController` 把两者都归到 `invalid_grant`，资源归属不匹配也用同一码。因此：
  - Resource 自身请求的认证或会话校验失败，照常返回本地 401；
  - 对 Identity 的委托调用、令牌交换、机器读取中的身份或认证失败，一律按依赖故障处理，沿用既有分类：取令牌协议拒绝及上游非业务 401/403 为 502，本地配置错误 500，连接失败 503，超时 504；
  - 已知的写入业务拒绝按 P5 转译；
  - 没有当前用户访问令牌时拒绝委托，不退回机器身份。
  测试同时断言前端没有被登出。
- P7：`PATCH` 一次只接受一个字段：多字段或未知字段 400，空对象 400。原因见 §2.4。

**收件人与服务端读取**

- P8：Given Resource 给收件人 B（不是当前用户）产出通知、邮件或按时区的统计。Then 按 B 在 Identity 的生效语言与时区，经机器身份读取，不需要 B 的令牌。
- P9：服务端按用户读偏好统一经模板接口 `IAccountPreferenceReader`。Identity / Standalone 的实现读本地设置；Resource 的实现读 Identity。现有 `UserTimeZoneProvider` 与按收件人取语言的调用点都改用它。

**Identity 的机器读取接口**

- P10：`GET /api/v1/internal/account-preferences/{tenant}/{userId}` 只接受 MachineOnly scope `account-preferences.read` 的机器令牌。
  - `tenant` 为 `host` 或租户 Id，先校验有效，再切换租户上下文并在 `requiresNew` 工作单元内读取。
  - 非法、停用、删除的租户返回 404，不退回宿主；用户不存在返回 404。
  - 响应带强 ETag，支持 `If-None-Match`。

**Resource 缓存**

- P11：缓存键为（租户，用户），值为完整表示（`preferences` 与 `preferenceOverrides`）加 ETag，保存在进程内。ETag 覆盖两部分：个人覆盖从 null 变成与生效值相同的显式值，ETag 也要变。读取分两类：
  - **当前用户的 `/me`**：自己发一次条件请求（有旧表示时带 `If-None-Match`，304 很轻），不受新鲜期限制，也不加入在途回源。
  - **服务端读取**（收件人文本、按时区统计等）：新鲜期 `FreshFor`（默认 30 秒）内直接用。
  - 过期且保留期 `RetainFor`（默认 24 小时）内还有旧表示时发条件请求：304 续期；200 替换；连接失败、超时、5xx 时用旧表示并记 Warning。
  - 没有旧表示时上游故障，抛「偏好不可用」：
    - `/me` 两个对象都为 null 并记 Warning，页面照常；
    - 服务端调用点按现有回落：语言用请求文化或现有默认，时区 UTC。
    不拿本地默认值冒充。
- P12：机器令牌 401/403 时，对该 key 做一次原子失效：代次加一并删除旧表示，记 Error，按依赖故障处理。
  - 失效前已发出的读取，返回 200 不得发布，304 不得续期，遇 5xx 不得用自己捕获的旧表示回落。
  - 失效之后发起的新读取成功，可以重新建立缓存。
  - 用例：「旧 200 在途 → 另一读取 401/403 → 旧 200 返回 → 新读取 5xx」与「旧回落请求在途 → 401/403 → 旧请求 5xx」，都不得恢复已作废的表示。
- P13：并发。每个 key 有代次，两种事件会推进它：本人经本实例写入成功（并用 Identity 返回的新表示与 ETag 替换旧表示），以及 P12 的机器认证失效。
  - 发起时代次已变的读取返回后，200 不写入缓存，304 不续期，异常不用旧表示回落；结果也不交给当前调用方或请求内记忆化；
  - 只有服务端读取之间按同 key、同代次合并回源。前台 `/me` 不合并，因为本进程的代次看不到其他实例或 Identity 上的写入。用例：「旧读取在途 → 本人写入成功 → 旧读取返回 → 再读」得到新值；同一请求内「读 → 写 → 读」也得到新值。同 key、同代次的并发回源合并为一次。

**前端状态**

- P13a：偏好是会话里独立的一份状态，不挂在当前用户模型上。
  - `AuthService.setCurrentUser()` 不碰偏好；个人资料、头像、邮箱确认等非 `/me` 响应写回用户时，不会抹掉已确认的偏好；资料写接口不必为此额外读取偏好。
  - 新主体建立时偏好不可用：清掉前一人的语言与时区，退回设备值。
  - 同一主体后台重查时不可用：保留已应用的值。
  - 偏好面板在「不可用」时显示「暂时无法读取」，不显示成「无覆盖」。
  - 语言、时区、外观三个字段的写入，不论来自偏好面板还是页头切换器，都进入会话偏好状态里同一个串行队列：前一项的成功表示应用完，才发下一项。原因：响应可能乱序，按「成功就应用完整表示」会让先完成的旧表示覆盖后完成的新表示。
    - 单次失败不卡住队列；
    - 主体离开时丢弃尚未发出的写入，执行前核对会话代次。
  - `PATCH` 成功：用响应里的完整表示应用，提示已保存；迟到的旧 `/me` 不得覆盖它（沿用写入序号）。
  - 明确的业务拒绝：保持原值，提示未保存。
  - 没有响应（连接中断、超时等）：写入可能已在 Identity 提交，保持原值，提示「未能确认保存」，不自动重放，由下一次 `/me` 重查对齐。
  - 迟到的旧 `/me` 与新写入、退出交错时，沿用写入序号与主体边界（F3）。

**OIDC**

- P14：Identity 授予 `profile` 时，userinfo 与 ID 令牌带 `locale`（生效语言）、`zoneinfo`（生效时区），未设置不发。

**界面**

- P15：个人设置的「偏好」改为专用面板，读 `/me`、写 `PATCH`，三种形态同一个组件。
  - 语言、时区、外观三行，顺序同 F9；只有一种语言时不出语言行（#190）。
  - 组织默认仍在 Identity / Standalone 的系统设置里维护；Resource 的系统设置不出 `Display.*`。
  - 界面改动先出对比页。

**形态**

- P16：
  - Standalone 与 Identity 行为等价于现状，只是前端来源换成 `/me`；
  - 本地化关闭、无前端（resource-capabilities-03）的形态裁剪正确；
  - 资源服务未开多租户时，也有指向 Identity 的客户端配置。

## 2. 方案

### 2.1 Identity 与 Standalone（偏好的持有方）

**偏好服务**

- Application 层新增 `AccountPreferenceAppService`。
- 读取：`GetForCurrentUserAsync`、`GetForUserAsync(userId)`，按现有设置回落（个人 → 组织 → 部署基线），返回生效值与个人覆盖。
- 写入：`PatchForCurrentUserAsync(input)`，单字段，经 `ISettingManager.SetAsync(..., SettingScopes.User, 当前用户)`，沿用现有定义校验、`TimeZoneSettingValidator`、锁、事件与操作记录。写入提交后重新读取，返回完整表示与 ETag。

**接口**

- `/me`（`UserOutputDto`）追加 `Preferences`、`PreferenceOverrides`。
- `PATCH /api/v1/auth/me/preferences`，媒体类型 `application/merge-patch+json`，同时接受 `application/json`。
- 机器读取接口：仅在 Identity 角色（`OpenIddictServer`）出现。
  - 在 `OAuthScopes.cs` 登记 MachineOnly scope `account-preferences.read`，用 `AddMachineScopePolicy` 建策略；
  - 控制器不标 `[Authorize]`，只标机器策略（`service-invocation.md` 的既有规则）；
  - 租户校验沿用模板 `AuthPrincipalFactory` 的做法：先校验目标租户、`Change` 上下文，再 `Begin(requiresNew: true)`。

**OIDC 声明**

- 现有声明出口在授予 `profile` 时追加 `locale`、`zoneinfo`。

**个人设置页**

- 不再用通用设置行渲染 `Display.*` 的个人层，改为偏好面板（P15）。
- 系统设置页的组织层不变。

### 2.2 Resource（偏好的读取方）

**设置定义**

- `Display.*` 设置定义、`DisplayDefaultsOptions` 与校验器在 `RemoteTokenAuth` 下裁掉（P3）。

**读取**

- Infrastructure 新增 Refit 客户端 `IIdentityAccountPreferencesClient`，读取挂 `AddClientCredentials()`，授予 `account-preferences.read`。
- `RemoteAccountPreferenceReader` 实现 `IAccountPreferenceReader`，缓存状态机见 P11–P13：
  - 用 `IMemoryCache` 加每个 key 的代次与在途合并；
  - 不放分布式缓存：值含个人偏好，失效依赖本进程的写入代次。
- 回源协调优先复用官方能力，只维护本场景需要的旧表示与代次。要验证：
  - 单个等待者取消不影响其他等待者；
  - 调用取消不被吞成 `/me` 的 null；
  - 共享的回源工厂不捕获已结束请求的 scoped 依赖；
  - 在途与代次状态能释放。
  这些不做成模板公共抽象。
- 机器读取接口的租户切换必须在实际读取的调用上下文内生效，不放在 await 之后已退出的辅助作用域里。
- 选项 `AccountPreferencesClientOptions`（`FreshFor`、`RetainFor`）：
  - 绑定 `Leistd:ServiceClients:Identity:AccountPreferences`，同时提供 `configure` 委托；
  - `RetainFor` 须大于 `FreshFor`，越界启动失败并报出实际键名。

**写入**

- 另一个 Refit 客户端挂 `AddTokenExchange()`，Audience 为 Identity 资源（`OAuth:Resource`），Scope 为 Identity API 的服务级 scope；Identity 登记该交换授予。
- 写入只代表当前主体本人：服务端从当前用户取主体，不接受目标用户参数。
- 错误转译（P5、P6）：
  - 远端写入的已知业务拒绝（400/403/404 且带错误码）转成同码同状态的本地 `BusinessException`，说明取 Identity 本地化后的 `detail`，不透传 URL 与原始片段；
  - 其余失败一律沿用 service-client 的既有映射（依赖故障），不转本地 401。

**接口与配置**

- `ResourceAuthController` 的 `/me` 追加 `Preferences`、`PreferenceOverrides`，取自读取器；无旧表示的上游故障时为 null。
- `PATCH /api/v1/auth/me/preferences` 转交 Identity，把 Identity 返回的完整表示原样返回，并写入本实例缓存（P13）。
- 配置节 `Leistd:ServiceClients:Identity` 在资源服务里无论是否开多租户都存在。读取与写入是两个命名客户端，共用地址节（`service-invocation.md` 已有写法）。

### 2.3 前端（三种形态同一份代码）

- `SessionContextService` 维护偏好状态（P13a），从 `/me` 应用语言、外观与时区；`SettingContextService` 不再提供这三项。
  - 日期格式 `displayLocale` 仍取前端实际活动语言，不直接读可能为 null 的 `preferences.language`；
  - 时区为 null 时沿用浏览器时区；
  - F3 页签可见重查 `/me`，配合 P11 的条件重验证完成跨站同步。
- 页头的语言切换器与主题切换都经会话偏好状态的写入队列发 `PATCH /me/preferences`，用成功响应更新偏好状态，不再另行刷新 `/me`。「失败不切换并提示」保留，提示按 P13a 区分业务拒绝与未能确认。
- 偏好面板（P15）替代通用设置行里的 `Display.*` 个人层；Mock 同步。
- `settings.md` §3、§4 改写：偏好来源为 `/me`；写入口径为 `PATCH /me/preferences`；资源服务不持有偏好。

### 2.4 备选与取舍

| 备选 | 结论 | 理由 |
| --- | --- | --- |
| 框架「远端设置」组件（第二稿） | 撤回 | 两轮审查都不通过；为一个场景改设置组件的核心契约与端点，复杂度过高 |
| 读取客户端与缓存放进 Identity 服务的 Client 包 | 不可行 | 资源服务独立生成，模板不知道 Identity 服务的包名；客户端写在资源服务的模板代码里，随模板同步 |
| 只用 OIDC 声明 | 不作为来源 | 令牌刷新前是旧值；外观不是标准声明；收件人路径没有令牌。声明仍下发，给第三方客户端 |
| 资源服务的个人页链出到 Identity、页头切换只作用于本会话 | 不采用 | 下游实测「刷新后又变回」令人困惑；转交写入成本可控 |
| `PATCH` 多字段原子写入 | 本轮不做 | 设置组件按项独立提交，多字段原子需要框架提供批量写入。界面一次只改一项，没有多字段的真实调用点；P7 单字段限制保证「全部校验通过后一次写入」不被违反 |
| `version` 字段 | 不做 | 内部接口用 HTTP ETag |

### 2.5 兼容与回滚

- 只改模板，不发 beta；随下一次 develop 合入 main 的模板正式版发布，需单独授权。
- 已有部署的资源服务里的本地个人值不再生效，文档写明可在 Identity 补写；不提供自动迁移。
- 前端 #189 的外观设置行与页头切换改为写 `PATCH`，这些尚未发正式版。

## 3. 计划

| 阶段 | 任务 | 验证 |
| --- | --- | --- |
| 0 | 本规格送 Codex 审查，明确通过后实施 | — |
| 1 | Identity / Standalone：偏好服务、`/me` 字段、`PATCH`、机器读取接口与 scope、OIDC 声明 | 集成测试（真实 PostgreSQL）：P1、P4、P5、P7、P10（宿主、两租户、独立库、非法租户）、P14 |
| 2 | Resource：裁掉 `Display.*`、读取客户端与缓存、写入转交与错误转译、`/me` 与 `PATCH`、`IAccountPreferenceReader` 接线 | 单测 P6（本服务真实未认证、交换或资源归属配置错误含 `invalid_grant`、取令牌连接与超时、Identity 非业务 401/403、已知业务拒绝）、P11–P13（新鲜期内切回、跨实例写后读、304 续期、5xx 用旧表示、401/403 清旧表示后 5xx 不恢复、取消语义）；集成测试 P2、P3、P8、P9（覆盖现有全部收件人语言调用点及不可用时的回落）；模板 OIDC 跨服务端到端加 P2、P4 |
| 3 | 前端：偏好状态与来源切到 `/me`、偏好面板、切换器写入、Mock、规范 | 对比页确认；前端单测覆盖 P13a 各场景与 P6 不登出；Mock 有头验证 |
| 交付 | 一个 PR（只改模板） | 生成 identity-all-features、resource-localization、resource、resource-capabilities-03、standalone 与无本地化形态；PR CI；Codex 审查；下游实施回执 |

## 4. 结果

### 方案审查记录

第一稿 Codex 审查不通过（2 项）；对 P6 的待定点给出了明确口径。本稿处理如下：

| # | 问题 | 本稿处理 |
| --- | --- | --- |
| R1 | 30 秒新鲜期与「切回页签就同步」冲突；本人写入只作废本实例 | 当前用户的 `/me` 总是条件重验证，服务端读取才用新鲜期；P2 补新鲜期内切回与跨实例场景（第三稿起不再有写后回读） |
| R2 | 「暂时读不到」与「已知未设置」未区分；非 `/me` 的用户写回可能抹掉偏好 | P1 成对的对象契约；P13a 偏好作为独立状态，按新主体、同主体分别处理（写入结果第三稿起由 `PATCH` 响应给出） |
| P6 | 现有信号无法区分用户失效与配置错误 | 远端认证失败一律按依赖故障处理，只有本服务自身认证失败才返回 401；删去无来源的标识分支 |
| 其他 | ETag 覆盖两部分、代次变化后不交给调用方、`displayLocale` 取活动语言、回源协调的取消语义、租户切换的作用域 | 写入 P11、P13、§2.2、§2.3 |

第二稿复审不通过（2 项边界），本稿处理如下：

| # | 问题 | 本稿处理 |
| --- | --- | --- |
| R1 | 前台 `/me` 可能加入写入前已发出的回源，拿到旧值 | 前台 `/me` 自己发条件请求，不合并在途回源；只有服务端读取之间合并；P2 补交错用例 |
| R2 | 清除覆盖成功但回读失败时，前端推不出新的生效值 | `PATCH` 响应直接返回 Identity 在提交后计算的完整表示，前端不回读；失败或无响应时保持原值，由下次 `/me` 对齐 |

第三稿复审：R1、R2 已闭合，新增两项并发约束，本稿处理如下：

| # | 问题 | 本稿处理 |
| --- | --- | --- |
| R3 | 401/403 只删缓存，在途的旧读取仍可重新发布或用旧值回落 | 401/403 做原子失效（代次加一并删除旧表示）；代次检查覆盖 200 发布、304 续期与异常回落；P12 补两个交错用例 |
| R4 | 不同控件的 `PATCH` 响应乱序，后到的旧完整表示会覆盖新值 | 三个字段的写入在会话偏好状态里共用一个串行队列；单次失败不卡住；主体离开丢弃未发出的写入 |
| 文字 | 「刷新 `/me`」等旧描述；没有响应时说成「未保存」 | 统一为 `/me` 初始化与重查、`PATCH` 响应更新；没有响应时提示「未能确认保存」 |

第四稿复审通过（2026-10-26），进入实施。
